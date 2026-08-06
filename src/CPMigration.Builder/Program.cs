using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;

var source = GetArg("--source") ?? Path.Combine(Directory.GetCurrentDirectory(), "output", "Migration", "CONSOLIDADO");
var output = GetArg("--output") ?? Path.Combine(Directory.GetCurrentDirectory(), "NovoERP");
Directory.CreateDirectory(output);

if (!Directory.Exists(source))
    throw new DirectoryNotFoundException($"Pasta consolidada não encontrada: {source}");

var files = Directory.EnumerateFiles(source, "*.ndjson.gz", SearchOption.TopDirectoryOnly).OrderBy(x => x).ToArray();
if (files.Length == 0)
    throw new InvalidOperationException($"Nenhum arquivo .ndjson.gz encontrado em: {source}");

EnsureFreeSpace(output, Math.Max(2L * 1024 * 1024 * 1024, files.Sum(f => new FileInfo(f).Length) * 3));

var databasePath = Path.Combine(output, "NovoERP.sqlite");
var partialPath = databasePath + ".partial";
if (File.Exists(partialPath)) File.Delete(partialPath);

long total = 0;
var domainCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
var databaseBuilt = false;

try
{
    await using (var connection = new SqliteConnection($"Data Source={partialPath};Mode=ReadWriteCreate;Cache=Private;Pooling=False"))
    {
        await connection.OpenAsync();
        await ExecuteAsync(connection, "PRAGMA journal_mode=DELETE; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA foreign_keys=OFF; PRAGMA cache_size=-131072;");
        await CreateSchemaAsync(connection);

        foreach (var file in files)
        {
            var domain = Path.GetFileName(file).Replace(".ndjson.gz", "", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"Convertendo {domain}...");

            await using var input = File.OpenRead(file);
            await using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

            long sourceLine = 0;
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                sourceLine++;
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var type = GetString(root, "type") ?? "generic";
                var sourceId = GetString(root, "sourceId");
                var core = root.TryGetProperty("core", out var coreNode) ? coreNode : default;

                await InsertDocumentIndexAsync(connection, transaction, domain, type, sourceId, Path.GetFileName(file), sourceLine);
                await InsertSpecializedAsync(connection, transaction, type, sourceId, core, Path.GetFileName(file), sourceLine);

                total++;
                domainCounts[domain] = domainCounts.GetValueOrDefault(domain) + 1;
                if (total % 10_000 == 0) Console.WriteLine($"  {total:N0} registros convertidos");
                if (total % 100_000 == 0) EnsureFreeSpace(output, 512L * 1024 * 1024);
            }

            await transaction.CommitAsync();
        }

        await ExecuteAsync(connection, "PRAGMA optimize; PRAGMA wal_checkpoint(TRUNCATE);");
        await connection.CloseAsync();
    }

    databaseBuilt = true;
    SqliteConnection.ClearAllPools();
    await MoveWithRetryAsync(partialPath, databasePath);

    var summary = new
    {
        generatedAt = DateTimeOffset.Now,
        source,
        database = databasePath,
        documents = total,
        mode = "compact-indexed",
        byDomain = domainCounts
    };

    await File.WriteAllTextAsync(Path.Combine(output, "builder-summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    await File.WriteAllTextAsync(Path.Combine(output, "LEIA-ME.txt"),
        "NovoERP.sqlite foi criado em modo compacto. O banco contém índices e campos principais normalizados. Os JSON completos continuam preservados nos arquivos .ndjson.gz de origem e podem ser localizados por source_file + source_line, evitando duplicar milhões de documentos dentro do SQLite.");

    Console.WriteLine();
    Console.WriteLine("CONCLUÍDO");
    Console.WriteLine($"Banco: {databasePath}");
    Console.WriteLine($"Registros indexados: {total:N0}");
}
catch
{
    SqliteConnection.ClearAllPools();
    if (!databaseBuilt)
    {
        try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch { }
    }
    else
    {
        Console.Error.WriteLine($"O banco foi totalmente gerado, mas não pôde ser renomeado. Arquivo preservado em: {partialPath}");
    }
    throw;
}

async Task MoveWithRetryAsync(string sourcePath, string destinationPath)
{
    Exception? lastError = null;
    for (var attempt = 1; attempt <= 15; attempt++)
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            File.Move(sourcePath, destinationPath);
            return;
        }
        catch (IOException ex)
        {
            lastError = ex;
            await Task.Delay(500 * attempt);
        }
    }

    throw new IOException($"Não foi possível finalizar o banco após várias tentativas. O arquivo completo foi preservado em: {sourcePath}", lastError);
}

async Task CreateSchemaAsync(SqliteConnection connection)
{
    const string sql = """
CREATE TABLE documents (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    domain TEXT NOT NULL,
    document_type TEXT NOT NULL,
    source_id TEXT,
    source_file TEXT NOT NULL,
    source_line INTEGER NOT NULL
);
CREATE INDEX idx_documents_domain ON documents(domain);
CREATE INDEX idx_documents_type ON documents(document_type);
CREATE INDEX idx_documents_source ON documents(source_id);

CREATE TABLE entities (source_id TEXT PRIMARY KEY, name TEXT, trade_name TEXT, cpf_cnpj TEXT, email TEXT, phone TEXT, source_file TEXT, source_line INTEGER);
CREATE TABLE products (source_id TEXT PRIMARY KEY, code TEXT, description TEXT, brand TEXT, source_file TEXT, source_line INTEGER);
CREATE TABLE stock (id INTEGER PRIMARY KEY AUTOINCREMENT, source_id TEXT, product_id TEXT, quantity TEXT, location TEXT, warehouse TEXT, source_file TEXT, source_line INTEGER);
CREATE TABLE financial (id INTEGER PRIMARY KEY AUTOINCREMENT, source_id TEXT, entity_id TEXT, amount TEXT, due_date TEXT, status TEXT, source_file TEXT, source_line INTEGER);
CREATE TABLE purchases (id INTEGER PRIMARY KEY AUTOINCREMENT, source_id TEXT, source_file TEXT, source_line INTEGER);
CREATE TABLE sales (id INTEGER PRIMARY KEY AUTOINCREMENT, source_id TEXT, source_file TEXT, source_line INTEGER);
CREATE TABLE fiscal (id INTEGER PRIMARY KEY AUTOINCREMENT, source_id TEXT, source_file TEXT, source_line INTEGER);
""";
    await ExecuteAsync(connection, sql);
}

async Task InsertDocumentIndexAsync(SqliteConnection connection, SqliteTransaction transaction, string domain, string type, string? sourceId, string sourceFile, long sourceLine)
{
    await using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = "INSERT INTO documents(domain,document_type,source_id,source_file,source_line) VALUES(@d,@t,@s,@f,@l)";
    command.Parameters.AddWithValue("@d", domain);
    command.Parameters.AddWithValue("@t", type);
    command.Parameters.AddWithValue("@s", (object?)sourceId ?? DBNull.Value);
    command.Parameters.AddWithValue("@f", sourceFile);
    command.Parameters.AddWithValue("@l", sourceLine);
    await command.ExecuteNonQueryAsync();
}

async Task InsertSpecializedAsync(SqliteConnection connection, SqliteTransaction transaction, string type, string? sourceId, JsonElement core, string sourceFile, long sourceLine)
{
    var table = type switch
    {
        "entity" => "entities",
        "product" => "products",
        "stock" => "stock",
        "financial" => "financial",
        "purchase" => "purchases",
        "sale" => "sales",
        "fiscal" => "fiscal",
        _ => null
    };
    if (table is null) return;

    await using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = table switch
    {
        "entities" => "INSERT OR REPLACE INTO entities VALUES(@s,@name,@trade,@doc,@email,@phone,@file,@line)",
        "products" => "INSERT OR REPLACE INTO products VALUES(@s,@code,@description,@brand,@file,@line)",
        "stock" => "INSERT INTO stock(source_id,product_id,quantity,location,warehouse,source_file,source_line) VALUES(@s,@product,@quantity,@location,@warehouse,@file,@line)",
        "financial" => "INSERT INTO financial(source_id,entity_id,amount,due_date,status,source_file,source_line) VALUES(@s,@entity,@amount,@due,@status,@file,@line)",
        _ => $"INSERT INTO {table}(source_id,source_file,source_line) VALUES(@s,@file,@line)"
    };

    command.Parameters.AddWithValue("@s", (object?)sourceId ?? DBNull.Value);
    command.Parameters.AddWithValue("@file", sourceFile);
    command.Parameters.AddWithValue("@line", sourceLine);
    Add(command, "@name", core, "name"); Add(command, "@trade", core, "tradeName"); Add(command, "@doc", core, "cpfCnpj");
    Add(command, "@email", core, "email"); Add(command, "@phone", core, "phone"); Add(command, "@code", core, "code");
    Add(command, "@description", core, "description"); Add(command, "@brand", core, "brand"); Add(command, "@product", core, "productId");
    Add(command, "@quantity", core, "quantity"); Add(command, "@location", core, "location"); Add(command, "@warehouse", core, "warehouse");
    Add(command, "@entity", core, "entityId"); Add(command, "@amount", core, "amount"); Add(command, "@due", core, "dueDate"); Add(command, "@status", core, "status");
    await command.ExecuteNonQueryAsync();
}

void Add(SqliteCommand command, string parameter, JsonElement element, string property)
{
    object value = element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var node) && node.ValueKind != JsonValueKind.Null
        ? node.ToString()
        : DBNull.Value;
    command.Parameters.AddWithValue(parameter, value);
}

string? GetString(JsonElement element, string property) => element.TryGetProperty(property, out var node) && node.ValueKind != JsonValueKind.Null ? node.ToString() : null;
async Task ExecuteAsync(SqliteConnection connection, string sql) { await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
string? GetArg(string name) { var index = Array.FindIndex(args, value => value.Equals(name, StringComparison.OrdinalIgnoreCase)); return index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : null; }

void EnsureFreeSpace(string path, long requiredBytes)
{
    var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? throw new IOException("Não foi possível identificar a unidade de saída.");
    var drive = new DriveInfo(root);
    if (drive.AvailableFreeSpace < requiredBytes)
        throw new IOException($"Espaço insuficiente em {root}. Livre: {drive.AvailableFreeSpace / 1024d / 1024d / 1024d:N2} GB; necessário para continuar: {requiredBytes / 1024d / 1024d / 1024d:N2} GB. Use --output em outro disco.");
}
