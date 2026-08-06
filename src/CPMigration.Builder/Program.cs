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

var databasePath = Path.Combine(output, "NovoERP.sqlite");
if (File.Exists(databasePath)) File.Delete(databasePath);

await using var connection = new SqliteConnection($"Data Source={databasePath}");
await connection.OpenAsync();
await ExecuteAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
await CreateSchemaAsync(connection);

long total = 0;
var domainCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

foreach (var file in files)
{
    var domain = Path.GetFileName(file).Replace(".ndjson.gz", "", StringComparison.OrdinalIgnoreCase);
    Console.WriteLine($"Convertendo {domain}...");

    await using var input = File.OpenRead(file);
    await using var gzip = new GZipStream(input, CompressionMode.Decompress);
    using var reader = new StreamReader(gzip);
    await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

    string? line;
    while ((line = await reader.ReadLineAsync()) is not null)
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var type = GetString(root, "type") ?? "generic";
        var sourceId = GetString(root, "sourceId");
        var coreJson = root.TryGetProperty("core", out var core) ? core.GetRawText() : "{}";
        var referencesJson = root.TryGetProperty("references", out var references) ? references.GetRawText() : "{}";
        var sourceJson = root.TryGetProperty("source", out var sourceNode) ? sourceNode.GetRawText() : "{}";

        await InsertDocumentAsync(connection, transaction, domain, type, sourceId, coreJson, referencesJson, sourceJson);
        await InsertSpecializedAsync(connection, transaction, type, sourceId, coreJson);
        total++;
        domainCounts[domain] = domainCounts.GetValueOrDefault(domain) + 1;

        if (total % 10000 == 0) Console.WriteLine($"  {total:N0} registros convertidos");
    }

    await transaction.CommitAsync();
}

var summary = new
{
    generatedAt = DateTimeOffset.Now,
    source,
    database = databasePath,
    documents = total,
    byDomain = domainCounts
};
await File.WriteAllTextAsync(Path.Combine(output, "builder-summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
await File.WriteAllTextAsync(Path.Combine(output, "LEIA-ME.txt"),
    "NovoERP.sqlite foi criado a partir dos arquivos .ndjson.gz consolidados. A tabela documents preserva todos os documentos normalizados. As tabelas entities, products, stock, financial, purchases, sales e fiscal recebem os campos principais disponíveis. Campos ainda não conhecidos permanecem em JSON para não perder dados.");

Console.WriteLine();
Console.WriteLine("CONCLUÍDO");
Console.WriteLine($"Banco: {databasePath}");
Console.WriteLine($"Registros: {total:N0}");

async Task CreateSchemaAsync(SqliteConnection connection)
{
    const string sql = """
CREATE TABLE documents (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    domain TEXT NOT NULL,
    document_type TEXT NOT NULL,
    source_id TEXT,
    core_json TEXT NOT NULL,
    references_json TEXT NOT NULL,
    source_json TEXT NOT NULL
);
CREATE INDEX idx_documents_domain ON documents(domain);
CREATE INDEX idx_documents_type ON documents(document_type);
CREATE INDEX idx_documents_source ON documents(source_id);

CREATE TABLE entities (source_id TEXT PRIMARY KEY, name TEXT, trade_name TEXT, cpf_cnpj TEXT, email TEXT, phone TEXT, raw_json TEXT NOT NULL);
CREATE TABLE products (source_id TEXT PRIMARY KEY, code TEXT, description TEXT, brand TEXT, raw_json TEXT NOT NULL);
CREATE TABLE stock (id INTEGER PRIMARY KEY AUTOINCREMENT, source_id TEXT, product_id TEXT, quantity TEXT, location TEXT, warehouse TEXT, raw_json TEXT NOT NULL);
CREATE TABLE financial (id INTEGER PRIMARY KEY AUTOINCREMENT, source_id TEXT, entity_id TEXT, amount TEXT, due_date TEXT, status TEXT, raw_json TEXT NOT NULL);
CREATE TABLE purchases (id INTEGER PRIMARY KEY AUTOINCREMENT, source_id TEXT, raw_json TEXT NOT NULL);
CREATE TABLE sales (id INTEGER PRIMARY KEY AUTOINCREMENT, source_id TEXT, raw_json TEXT NOT NULL);
CREATE TABLE fiscal (id INTEGER PRIMARY KEY AUTOINCREMENT, source_id TEXT, raw_json TEXT NOT NULL);
""";
    await ExecuteAsync(connection, sql);
}

async Task InsertDocumentAsync(SqliteConnection connection, SqliteTransaction transaction, string domain, string type, string? sourceId, string coreJson, string referencesJson, string sourceJson)
{
    await using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = "INSERT INTO documents(domain,document_type,source_id,core_json,references_json,source_json) VALUES(@d,@t,@s,@c,@r,@o)";
    command.Parameters.AddWithValue("@d", domain);
    command.Parameters.AddWithValue("@t", type);
    command.Parameters.AddWithValue("@s", (object?)sourceId ?? DBNull.Value);
    command.Parameters.AddWithValue("@c", coreJson);
    command.Parameters.AddWithValue("@r", referencesJson);
    command.Parameters.AddWithValue("@o", sourceJson);
    await command.ExecuteNonQueryAsync();
}

async Task InsertSpecializedAsync(SqliteConnection connection, SqliteTransaction transaction, string type, string? sourceId, string coreJson)
{
    using var doc = JsonDocument.Parse(coreJson);
    var core = doc.RootElement;
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
        "entities" => "INSERT OR REPLACE INTO entities VALUES(@s,@name,@trade,@doc,@email,@phone,@raw)",
        "products" => "INSERT OR REPLACE INTO products VALUES(@s,@code,@description,@brand,@raw)",
        "stock" => "INSERT INTO stock(source_id,product_id,quantity,location,warehouse,raw_json) VALUES(@s,@product,@quantity,@location,@warehouse,@raw)",
        "financial" => "INSERT INTO financial(source_id,entity_id,amount,due_date,status,raw_json) VALUES(@s,@entity,@amount,@due,@status,@raw)",
        _ => $"INSERT INTO {table}(source_id,raw_json) VALUES(@s,@raw)"
    };

    command.Parameters.AddWithValue("@s", (object?)sourceId ?? DBNull.Value);
    command.Parameters.AddWithValue("@raw", coreJson);
    Add(command, "@name", core, "name"); Add(command, "@trade", core, "tradeName"); Add(command, "@doc", core, "cpfCnpj");
    Add(command, "@email", core, "email"); Add(command, "@phone", core, "phone"); Add(command, "@code", core, "code");
    Add(command, "@description", core, "description"); Add(command, "@brand", core, "brand"); Add(command, "@product", core, "productId");
    Add(command, "@quantity", core, "quantity"); Add(command, "@location", core, "location"); Add(command, "@warehouse", core, "warehouse");
    Add(command, "@entity", core, "entityId"); Add(command, "@amount", core, "amount"); Add(command, "@due", core, "dueDate"); Add(command, "@status", core, "status");
    await command.ExecuteNonQueryAsync();
}

void Add(SqliteCommand command, string parameter, JsonElement element, string property)
{
    object value = element.TryGetProperty(property, out var node) && node.ValueKind != JsonValueKind.Null ? node.ToString() : DBNull.Value;
    command.Parameters.AddWithValue(parameter, value);
}

string? GetString(JsonElement element, string property) => element.TryGetProperty(property, out var node) && node.ValueKind != JsonValueKind.Null ? node.ToString() : null;
async Task ExecuteAsync(SqliteConnection connection, string sql) { await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
string? GetArg(string name) { var index = Array.FindIndex(args, value => value.Equals(name, StringComparison.OrdinalIgnoreCase)); return index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : null; }
