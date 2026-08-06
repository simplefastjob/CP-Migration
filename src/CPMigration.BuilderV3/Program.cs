using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

Console.OutputEncoding = Encoding.UTF8;
var root = Directory.GetCurrentDirectory();
var sourcePath = Arg("--source") ?? Path.Combine(root, "output", "erp_intermediario.sqlite");
var outputDirectory = Arg("--output") ?? Path.Combine(root, "NovoERP-V3");
Directory.CreateDirectory(outputDirectory);

var finalPath = Path.Combine(outputDirectory, "NovoERP_V3.sqlite");
var partialPath = finalPath + ".partial";
var logPath = Path.Combine(outputDirectory, "BUILDER_V3.log");
var reportPath = Path.Combine(outputDirectory, "RELATORIO_BUILDER_V3.json");
var reviewPath = Path.Combine(outputDirectory, "REVISAO.csv");

await using var log = new StreamWriter(logPath, false, new UTF8Encoding(true)) { AutoFlush = true };
var watch = Stopwatch.StartNew();
var report = new BuildReport { StartedAt = DateTimeOffset.Now, Source = sourcePath, Output = finalPath };

try
{
    if (!File.Exists(sourcePath)) throw new FileNotFoundException("erp_intermediario.sqlite não encontrado.", sourcePath);
    EnsureSpace(outputDirectory, 2L * 1024 * 1024 * 1024);
    if (File.Exists(partialPath)) File.Delete(partialPath);

    await Log("START", $"Origem: {sourcePath}");
    await using var source = new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly;Cache=Private;Pooling=False");
    await using var target = new SqliteConnection($"Data Source={partialPath};Mode=ReadWriteCreate;Cache=Private;Pooling=False");
    await source.OpenAsync();
    await target.OpenAsync();
    await Exec(source, "PRAGMA query_only=ON; PRAGMA temp_store=MEMORY;");
    await Exec(target, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=OFF; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-131072;");
    await CreateSchema(target);

    var sourceTables = await ReadTableNames(source);
    await using var review = new StreamWriter(reviewPath, false, new UTF8Encoding(true)) { AutoFlush = true };
    await review.WriteLineAsync("modulo;tabela;chave;motivo");

    await RunModule("CLIENTES", BuildClients);
    await RunModule("PRODUTOS", BuildProducts);
    await RunModule("ESTOQUE", BuildStock);
    await RunModule("FORNECEDORES", BuildSuppliers);

    await CreateIndexes(target);
    await Validate(target, report);
    await Exec(target, "PRAGMA optimize; PRAGMA wal_checkpoint(TRUNCATE);");
    await target.CloseAsync();
    await source.CloseAsync();
    SqliteConnection.ClearAllPools();

    await MoveWithRetry(partialPath, finalPath);
    report.FinishedAt = DateTimeOffset.Now;
    report.Duration = watch.Elapsed.ToString("c");
    report.Success = true;
    await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions()));
    await File.WriteAllTextAsync(Path.Combine(outputDirectory, "LEIA-ME.txt"),
        "NovoERP_V3.sqlite é a primeira versão especializada do Builder V3. Ela reconstrói clientes, fornecedores, produtos e estoque diretamente do erp_intermediario.sqlite. Campos não reconhecidos continuam preservados em raw_json e ocorrências incompletas ficam em REVISAO.csv.");

    Console.WriteLine();
    Console.WriteLine("BUILDER V3 CONCLUÍDO");
    Console.WriteLine($"Banco: {finalPath}");
    Console.WriteLine($"Tempo: {watch.Elapsed:hh\\:mm\\:ss}");
    Environment.ExitCode = 0;

    async Task RunModule(string module, Func<Task<ModuleResult>> builder)
    {
        Console.WriteLine($"[{module}] iniciando...");
        await Log("MODULE_START", module);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await builder();
            result.Duration = sw.Elapsed.ToString("c");
            report.Modules[module] = result;
            await SaveCheckpoint(target, module, "CONCLUIDO", result.Read, result.Written, null);
            await Log("MODULE_DONE", $"{module}: lidos {result.Read:N0}; gravados {result.Written:N0}; revisão {result.Review:N0}; tempo {sw.Elapsed:hh\\:mm\\:ss}");
        }
        catch (Exception ex)
        {
            await SaveCheckpoint(target, module, "ERRO", 0, 0, ex.Message);
            await Log("MODULE_ERROR", $"{module}: {ex}");
            throw;
        }
    }

    async Task<ModuleResult> BuildClients()
    {
        const string table = "T_RRPARTS__PAR_ENTIDADES";
        if (!sourceTables.Contains(table)) return Missing(table);
        var result = new ModuleResult();
        await using var tx = (SqliteTransaction)await target.BeginTransactionAsync();
        await using var reader = await SelectAll(source, table);
        var ordinals = Ordinals(reader);
        while (await reader.ReadAsync())
        {
            result.Read++;
            var id = Value(reader, ordinals, "PARENTD_ID", "ID");
            if (string.IsNullOrWhiteSpace(id)) { result.Review++; await Review("CLIENTES", table, "", "Sem PARENTD_ID"); continue; }
            var raw = RowJson(reader);
            await Insert(target, tx, """
INSERT OR REPLACE INTO clientes
(id, nome, fantasia, cpf_cnpj, email, telefone, data_cadastro, limite_credito, saldo, ultima_compra, ultima_venda, tipo_pessoa, raw_json)
VALUES(@id,@nome,@fantasia,@doc,@email,@fone,@cad,@limite,@saldo,@compra,@venda,@tipo,@raw)
""", new()
            {
                ["@id"] = id,
                ["@nome"] = Value(reader, ordinals, "NOME_ENTIDADE", "NOME_RAZAO", "NOME", "DESC_ENTIDADE"),
                ["@fantasia"] = Value(reader, ordinals, "NOME_FANTASIA", "DESC_FANTASIA"),
                ["@doc"] = Value(reader, ordinals, "NUMR_CGC", "NUMR_CPF", "CNPJ", "CPF"),
                ["@email"] = Value(reader, ordinals, "NOME_EMAIL", "DESC_EMAIL", "EMAIL"),
                ["@fone"] = Value(reader, ordinals, "NUMR_TELEFONE", "NUMR_FONE", "TELEFONE", "FONE"),
                ["@cad"] = Value(reader, ordinals, "DATA_CADASTRO", "DTHR_CADASTRO"),
                ["@limite"] = DecimalValue(reader, ordinals, "VALR_LIMITE", "VALR_LIMITE_CREDITO"),
                ["@saldo"] = DecimalValue(reader, ordinals, "VALR_SALDO"),
                ["@compra"] = Value(reader, ordinals, "DATA_ULTIMA_COMPRA"),
                ["@venda"] = Value(reader, ordinals, "DATA_ULTIMA_VENDA"),
                ["@tipo"] = Value(reader, ordinals, "INDR_PESSOA", "TIPO_PESSOA"),
                ["@raw"] = raw
            });
            result.Written++;
            if (result.Written % 1000 == 0) Console.WriteLine($"  Clientes: {result.Written:N0}");
        }
        await tx.CommitAsync();
        await AttachPersonSpecializations(result);
        return result;
    }

    async Task AttachPersonSpecializations(ModuleResult result)
    {
        foreach (var spec in new[] { ("T_RRPARTS__PAR_PESSOA_FISICA", "PF"), ("T_RRPARTS__PAR_PESSOA_JURIDICA", "PJ") })
        {
            if (!sourceTables.Contains(spec.Item1)) continue;
            await using var tx = (SqliteTransaction)await target.BeginTransactionAsync();
            await using var reader = await SelectAll(source, spec.Item1);
            var ordinals = Ordinals(reader);
            while (await reader.ReadAsync())
            {
                var id = Value(reader, ordinals, "PARENTD_ID");
                if (string.IsNullOrWhiteSpace(id)) continue;
                await Insert(target, tx, "UPDATE clientes SET tipo_pessoa=@tipo, complemento_json=@json WHERE id=@id", new()
                {
                    ["@tipo"] = spec.Item2, ["@json"] = RowJson(reader), ["@id"] = id
                });
            }
            await tx.CommitAsync();
        }
    }

    async Task<ModuleResult> BuildProducts()
    {
        const string table = "T_RRPARTS__COM_PRODUTOS";
        if (!sourceTables.Contains(table)) return Missing(table);
        var result = new ModuleResult();
        await using var tx = (SqliteTransaction)await target.BeginTransactionAsync();
        await using var reader = await SelectAll(source, table);
        var ordinals = Ordinals(reader);
        while (await reader.ReadAsync())
        {
            result.Read++;
            var id = Value(reader, ordinals, "PCIPMAT_ID", "COMPROD_ID", "ID");
            if (string.IsNullOrWhiteSpace(id)) { result.Review++; await Review("PRODUTOS", table, "", "Sem PCIPMAT_ID/COMPROD_ID"); continue; }
            await Insert(target, tx, """
INSERT OR REPLACE INTO produtos
(id, comprod_id, codigo, descricao, descricao_reduzida, marca_id, grupo_id, ncm, ean, unidade, ativo, raw_json)
VALUES(@id,@comprod,@codigo,@descricao,@reduzida,@marca,@grupo,@ncm,@ean,@unidade,@ativo,@raw)
""", new()
            {
                ["@id"] = id,
                ["@comprod"] = Value(reader, ordinals, "COMPROD_ID"),
                ["@codigo"] = Value(reader, ordinals, "CODG_PRODUTO", "CODIGO", "CODG_ITEM"),
                ["@descricao"] = Value(reader, ordinals, "DESC_PRODUTO", "DESCRICAO", "NOME_PRODUTO"),
                ["@reduzida"] = Value(reader, ordinals, "DESC_REDUZIDA", "DESC_RESUMIDA"),
                ["@marca"] = Value(reader, ordinals, "COMMARC_ID", "MARCA_ID"),
                ["@grupo"] = Value(reader, ordinals, "COMGRUP_ID", "GRUPO_ID"),
                ["@ncm"] = Value(reader, ordinals, "CODG_NCM", "NCM"),
                ["@ean"] = Value(reader, ordinals, "CODG_EAN", "EAN", "CODG_BARRAS"),
                ["@unidade"] = Value(reader, ordinals, "CODG_UNIDADE", "UNIDADE"),
                ["@ativo"] = Value(reader, ordinals, "INDR_ATIVO", "INDR_INATIVO"),
                ["@raw"] = RowJson(reader)
            });
            result.Written++;
            if (result.Written % 1000 == 0) Console.WriteLine($"  Produtos: {result.Written:N0}");
        }
        await tx.CommitAsync();
        await CopyProductChildren(result);
        return result;
    }

    async Task CopyProductChildren(ModuleResult result)
    {
        var definitions = new[]
        {
            new ChildDefinition("T_RRPARTS__COM_PRODUTO_CODIGO", "produto_codigos", "COMPROD_ID", ["CODG_PRODUTO", "CODIGO", "CODG_BARRAS"]),
            new ChildDefinition("T_RRPARTS__COM_PRECO_PRODUTO_ATUAL", "produto_precos", "COMPROD_ID", ["VALR_PRECO", "VALR_VENDA", "VALR_CUSTO", "DATA_INICIAL", "DATA_VIGENCIA"]),
            new ChildDefinition("T_RRPARTS__COM_PRECO_PRODUTO", "produto_precos_historico", "COMPROD_ID", ["VALR_PRECO", "VALR_VENDA", "VALR_CUSTO", "DATA_INICIAL", "DATA_FINAL"]),
            new ChildDefinition("T_RRPARTS__COM_PRODUTO_SIMILAR", "produto_similares", "COMPROD_ID", ["COMPROD_ID_SIMILAR"]),
            new ChildDefinition("T_RRPARTS__PCI_APLICACAO_RESUMO", "produto_aplicacoes", "COMPROD_ID", ["DESC_APLICACAO", "APLICACAO"]),
            new ChildDefinition("T_RRPARTS__PCI_FORNECEDOR_PRODUTO", "produto_fornecedores", "PCIPMAT_ID", ["PARENTD_ID", "CODG_PRODUTO_FORNECEDOR", "VALR_CUSTO"])
        };
        foreach (var d in definitions) await CopyChild(d, result);
    }

    async Task CopyChild(ChildDefinition definition, ModuleResult result)
    {
        if (!sourceTables.Contains(definition.SourceTable)) return;
        await using var tx = (SqliteTransaction)await target.BeginTransactionAsync();
        await using var reader = await SelectAll(source, definition.SourceTable);
        var ordinals = Ordinals(reader);
        while (await reader.ReadAsync())
        {
            var productId = Value(reader, ordinals, definition.KeyColumn);
            if (string.IsNullOrWhiteSpace(productId)) continue;
            var payload = definition.Fields.ToDictionary(x => x, x => (object?)Value(reader, ordinals, x));
            await Insert(target, tx, $"INSERT INTO {definition.TargetTable}(produto_id, dados_json, source_table) VALUES(@id,@json,@source)", new()
            {
                ["@id"] = productId,
                ["@json"] = JsonSerializer.Serialize(payload),
                ["@source"] = definition.SourceTable
            });
        }
        await tx.CommitAsync();
    }

    async Task<ModuleResult> BuildStock()
    {
        const string table = "T_RRPARTS__PCI_ITEM_DEPOSITO";
        if (!sourceTables.Contains(table)) return Missing(table);
        var result = new ModuleResult();
        await using var tx = (SqliteTransaction)await target.BeginTransactionAsync();
        await using var reader = await SelectAll(source, table);
        var ordinals = Ordinals(reader);
        while (await reader.ReadAsync())
        {
            result.Read++;
            var product = Value(reader, ordinals, "PCIPMAT_ID");
            if (string.IsNullOrWhiteSpace(product)) { result.Review++; await Review("ESTOQUE", table, "", "Sem PCIPMAT_ID"); continue; }
            await Insert(target, tx, """
INSERT INTO estoque(produto_id, deposito_id, empresa_id, quantidade, reservado, disponivel, custo_medio, raw_json)
VALUES(@produto,@deposito,@empresa,@qtd,@reservado,@disponivel,@custo,@raw)
""", new()
            {
                ["@produto"] = product,
                ["@deposito"] = Value(reader, ordinals, "PCIDEPO_ID"),
                ["@empresa"] = Value(reader, ordinals, "PAREMPR_ID"),
                ["@qtd"] = DecimalValue(reader, ordinals, "QTDE_ESTOQUE", "QTDE_SALDO", "QTDE_ATUAL", "QTDE_PRODUTO"),
                ["@reservado"] = DecimalValue(reader, ordinals, "QTDE_RESERVADA", "QTDE_RESERVADO"),
                ["@disponivel"] = DecimalValue(reader, ordinals, "QTDE_DISPONIVEL"),
                ["@custo"] = DecimalValue(reader, ordinals, "VALR_CUSTO_MEDIO", "VALR_CUSTO"),
                ["@raw"] = RowJson(reader)
            });
            result.Written++;
        }
        await tx.CommitAsync();
        await CopyLocations(result);
        return result;
    }

    async Task CopyLocations(ModuleResult result)
    {
        const string table = "T_RRPARTS__PCI_LOCACAO";
        if (!sourceTables.Contains(table)) return;
        await using var tx = (SqliteTransaction)await target.BeginTransactionAsync();
        await using var reader = await SelectAll(source, table);
        var ordinals = Ordinals(reader);
        while (await reader.ReadAsync())
        {
            var product = Value(reader, ordinals, "PCIPMAT_ID");
            if (string.IsNullOrWhiteSpace(product)) continue;
            await Insert(target, tx, """
INSERT INTO localizacoes(produto_id, deposito_id, rua, prateleira, nivel, posicao, quantidade, raw_json)
VALUES(@produto,@deposito,@rua,@prateleira,@nivel,@posicao,@qtd,@raw)
""", new()
            {
                ["@produto"] = product,
                ["@deposito"] = Value(reader, ordinals, "PCIDEPO_ID"),
                ["@rua"] = Value(reader, ordinals, "CODG_RUA", "DESC_RUA", "RUA"),
                ["@prateleira"] = Value(reader, ordinals, "CODG_PRATELEIRA", "PRATELEIRA"),
                ["@nivel"] = Value(reader, ordinals, "CODG_NIVEL", "NIVEL"),
                ["@posicao"] = Value(reader, ordinals, "CODG_POSICAO", "POSICAO", "DESC_LOCACAO"),
                ["@qtd"] = DecimalValue(reader, ordinals, "QTDE_PRODUTO", "QTDE_LOCACAO"),
                ["@raw"] = RowJson(reader)
            });
        }
        await tx.CommitAsync();
    }

    async Task<ModuleResult> BuildSuppliers()
    {
        var result = new ModuleResult();
        var table = sourceTables.Contains("T_RRPARTS__PAR_FORN_ENTIDADE") ? "T_RRPARTS__PAR_FORN_ENTIDADE" : "T_RRPARTS__PAR_FORNECEDOR";
        if (!sourceTables.Contains(table)) return Missing(table);
        await using var tx = (SqliteTransaction)await target.BeginTransactionAsync();
        await using var reader = await SelectAll(source, table);
        var ordinals = Ordinals(reader);
        while (await reader.ReadAsync())
        {
            result.Read++;
            var entityId = Value(reader, ordinals, "PARENTD_ID", "ID");
            if (string.IsNullOrWhiteSpace(entityId)) { result.Review++; continue; }
            await Insert(target, tx, "INSERT OR REPLACE INTO fornecedores(entidade_id, codigo, ativo, raw_json) VALUES(@id,@codigo,@ativo,@raw)", new()
            {
                ["@id"] = entityId,
                ["@codigo"] = Value(reader, ordinals, "CODG_FORNECEDOR", "CODIGO"),
                ["@ativo"] = Value(reader, ordinals, "INDR_ATIVO", "INDR_INATIVO"),
                ["@raw"] = RowJson(reader)
            });
            result.Written++;
        }
        await tx.CommitAsync();
        return result;
    }

    ModuleResult Missing(string table)
    {
        report.Warnings.Add($"Tabela não encontrada: {table}");
        return new ModuleResult { Notes = $"Tabela não encontrada: {table}" };
    }

    async Task Review(string module, string table, string key, string reason)
        => await review.WriteLineAsync($"{Csv(module)};{Csv(table)};{Csv(key)};{Csv(reason)}");
}
catch (Exception ex)
{
    report.FinishedAt = DateTimeOffset.Now;
    report.Duration = watch.Elapsed.ToString("c");
    report.Success = false;
    report.FatalError = ex.ToString();
    try { await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions())); } catch { }
    await Log("FATAL", ex.ToString());
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}

async Task CreateSchema(SqliteConnection db)
{
    await Exec(db, """
CREATE TABLE metadata(key TEXT PRIMARY KEY, value TEXT);
CREATE TABLE checkpoints(module TEXT PRIMARY KEY, status TEXT NOT NULL, read_count INTEGER NOT NULL, written_count INTEGER NOT NULL, error TEXT, updated_at TEXT NOT NULL);
CREATE TABLE clientes(id TEXT PRIMARY KEY, nome TEXT, fantasia TEXT, cpf_cnpj TEXT, email TEXT, telefone TEXT, data_cadastro TEXT, limite_credito REAL, saldo REAL, ultima_compra TEXT, ultima_venda TEXT, tipo_pessoa TEXT, complemento_json TEXT, raw_json TEXT NOT NULL);
CREATE TABLE fornecedores(entidade_id TEXT PRIMARY KEY, codigo TEXT, ativo TEXT, raw_json TEXT NOT NULL);
CREATE TABLE produtos(id TEXT PRIMARY KEY, comprod_id TEXT, codigo TEXT, descricao TEXT, descricao_reduzida TEXT, marca_id TEXT, grupo_id TEXT, ncm TEXT, ean TEXT, unidade TEXT, ativo TEXT, raw_json TEXT NOT NULL);
CREATE TABLE produto_codigos(id INTEGER PRIMARY KEY AUTOINCREMENT, produto_id TEXT, dados_json TEXT, source_table TEXT);
CREATE TABLE produto_precos(id INTEGER PRIMARY KEY AUTOINCREMENT, produto_id TEXT, dados_json TEXT, source_table TEXT);
CREATE TABLE produto_precos_historico(id INTEGER PRIMARY KEY AUTOINCREMENT, produto_id TEXT, dados_json TEXT, source_table TEXT);
CREATE TABLE produto_similares(id INTEGER PRIMARY KEY AUTOINCREMENT, produto_id TEXT, dados_json TEXT, source_table TEXT);
CREATE TABLE produto_aplicacoes(id INTEGER PRIMARY KEY AUTOINCREMENT, produto_id TEXT, dados_json TEXT, source_table TEXT);
CREATE TABLE produto_fornecedores(id INTEGER PRIMARY KEY AUTOINCREMENT, produto_id TEXT, dados_json TEXT, source_table TEXT);
CREATE TABLE estoque(id INTEGER PRIMARY KEY AUTOINCREMENT, produto_id TEXT NOT NULL, deposito_id TEXT, empresa_id TEXT, quantidade REAL, reservado REAL, disponivel REAL, custo_medio REAL, raw_json TEXT NOT NULL);
CREATE TABLE localizacoes(id INTEGER PRIMARY KEY AUTOINCREMENT, produto_id TEXT NOT NULL, deposito_id TEXT, rua TEXT, prateleira TEXT, nivel TEXT, posicao TEXT, quantidade REAL, raw_json TEXT NOT NULL);
""");
}

async Task CreateIndexes(SqliteConnection db)
{
    await Exec(db, """
CREATE INDEX idx_clientes_doc ON clientes(cpf_cnpj);
CREATE INDEX idx_produtos_codigo ON produtos(codigo);
CREATE INDEX idx_produtos_comprod ON produtos(comprod_id);
CREATE INDEX idx_estoque_produto ON estoque(produto_id);
CREATE INDEX idx_localizacoes_produto ON localizacoes(produto_id);
CREATE INDEX idx_precos_produto ON produto_precos(produto_id);
CREATE INDEX idx_fornecedor_produto ON produto_fornecedores(produto_id);
""");
}

async Task Validate(SqliteConnection target, BuildReport report)
{
    foreach (var table in new[] { "clientes", "fornecedores", "produtos", "estoque", "localizacoes", "produto_precos", "produto_aplicacoes" })
        report.TargetCounts[table] = await ScalarLong(target, $"SELECT COUNT(*) FROM {table}");
    report.Validation["produtos_sem_codigo"] = await ScalarLong(target, "SELECT COUNT(*) FROM produtos WHERE codigo IS NULL OR TRIM(codigo)='' ");
    report.Validation["produtos_sem_descricao"] = await ScalarLong(target, "SELECT COUNT(*) FROM produtos WHERE descricao IS NULL OR TRIM(descricao)='' ");
    report.Validation["estoque_sem_produto"] = await ScalarLong(target, "SELECT COUNT(*) FROM estoque e LEFT JOIN produtos p ON p.id=e.produto_id WHERE p.id IS NULL");
    report.Validation["fornecedores_sem_entidade"] = await ScalarLong(target, "SELECT COUNT(*) FROM fornecedores f LEFT JOIN clientes c ON c.id=f.entidade_id WHERE c.id IS NULL");
}

async Task<HashSet<string>> ReadTableNames(SqliteConnection db)
{
    var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    await using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) result.Add(reader.GetString(0));
    return result;
}

async Task<SqliteDataReader> SelectAll(SqliteConnection db, string table)
{
    var cmd = db.CreateCommand();
    cmd.CommandText = $"SELECT * FROM {Q(table)}";
    cmd.CommandTimeout = 0;
    return await cmd.ExecuteReaderAsync();
}

Dictionary<string, int> Ordinals(SqliteDataReader reader)
{
    var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < reader.FieldCount; i++) result[reader.GetName(i)] = i;
    return result;
}

string? Value(SqliteDataReader reader, Dictionary<string, int> ordinals, params string[] candidates)
{
    foreach (var name in candidates)
        if (ordinals.TryGetValue(name, out var i) && !reader.IsDBNull(i))
        {
            var value = Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)?.Trim();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
    return null;
}

double? DecimalValue(SqliteDataReader reader, Dictionary<string, int> ordinals, params string[] candidates)
{
    var text = Value(reader, ordinals, candidates);
    if (string.IsNullOrWhiteSpace(text)) return null;
    if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var invariant)) return invariant;
    if (double.TryParse(text, NumberStyles.Any, new CultureInfo("pt-BR"), out var br)) return br;
    return null;
}

string RowJson(SqliteDataReader reader)
{
    var row = new Dictionary<string, object?>();
    for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString();
    return JsonSerializer.Serialize(row);
}

async Task Insert(SqliteConnection db, SqliteTransaction tx, string sql, Dictionary<string, object?> values)
{
    await using var cmd = db.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = sql;
    foreach (var item in values) cmd.Parameters.AddWithValue(item.Key, item.Value ?? DBNull.Value);
    await cmd.ExecuteNonQueryAsync();
}

async Task SaveCheckpoint(SqliteConnection db, string module, string status, long read, long written, string? error)
{
    await using var cmd = db.CreateCommand();
    cmd.CommandText = "INSERT OR REPLACE INTO checkpoints(module,status,read_count,written_count,error,updated_at) VALUES(@m,@s,@r,@w,@e,@u)";
    cmd.Parameters.AddWithValue("@m", module); cmd.Parameters.AddWithValue("@s", status); cmd.Parameters.AddWithValue("@r", read); cmd.Parameters.AddWithValue("@w", written); cmd.Parameters.AddWithValue("@e", (object?)error ?? DBNull.Value); cmd.Parameters.AddWithValue("@u", DateTimeOffset.Now.ToString("O"));
    await cmd.ExecuteNonQueryAsync();
}

async Task Exec(SqliteConnection db, string sql) { await using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.CommandTimeout = 0; await cmd.ExecuteNonQueryAsync(); }
async Task<long> ScalarLong(SqliteConnection db, string sql) { await using var cmd = db.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0); }
string Q(string name) => $"\"{name.Replace("\"", "\"\"")}\"";
string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
string? Arg(string name) { var i = Array.FindIndex(args, x => x.Equals(name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : null; }
async Task Log(string code, string message) { var line = $"{DateTimeOffset.Now:O} [{code}] {message}"; Console.WriteLine(line); await log.WriteLineAsync(line); }
JsonSerializerOptions JsonOptions() => new() { WriteIndented = true };

void EnsureSpace(string path, long required)
{
    var rootPath = Path.GetPathRoot(Path.GetFullPath(path)) ?? throw new IOException("Unidade de saída não identificada.");
    var drive = new DriveInfo(rootPath);
    if (drive.AvailableFreeSpace < required) throw new IOException($"Espaço insuficiente em {rootPath}. Livre: {drive.AvailableFreeSpace / 1024d / 1024d / 1024d:N2} GB; mínimo: {required / 1024d / 1024d / 1024d:N2} GB.");
}

async Task MoveWithRetry(string source, string destination)
{
    if (File.Exists(destination)) File.Delete(destination);
    Exception? last = null;
    for (var i = 1; i <= 15; i++)
    {
        try { File.Move(source, destination); return; }
        catch (IOException ex) { last = ex; GC.Collect(); GC.WaitForPendingFinalizers(); await Task.Delay(i * 300); }
    }
    throw new IOException("Não foi possível finalizar o banco após fechar as conexões.", last);
}

sealed class ModuleResult { public long Read { get; set; } public long Written { get; set; } public long Review { get; set; } public string Duration { get; set; } = ""; public string? Notes { get; set; } }
sealed class BuildReport
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Source { get; set; } = "";
    public string Output { get; set; } = "";
    public string Duration { get; set; } = "";
    public bool Success { get; set; }
    public string? FatalError { get; set; }
    public Dictionary<string, ModuleResult> Modules { get; set; } = new();
    public Dictionary<string, long> TargetCounts { get; set; } = new();
    public Dictionary<string, long> Validation { get; set; } = new();
    public List<string> Warnings { get; set; } = [];
}
sealed record ChildDefinition(string SourceTable, string TargetTable, string KeyColumn, string[] Fields);
