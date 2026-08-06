using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CPMigration.BuilderV4.Core;

public interface IBuilderModule
{
    string Name { get; }
    Task<ModuleResult> ExecuteAsync(BuilderContext context, CancellationToken ct);
}

public sealed class BuilderContext
{
    public required SqliteConnection Source { get; init; }
    public required SqliteConnection Target { get; init; }
    public required HashSet<string> SourceTables { get; init; }
    public required StreamWriter Review { get; init; }
    public required Func<string, string, Task> LogAsync { get; init; }

    public bool HasTable(string name) => SourceTables.Contains(name);

    public async Task<SqliteDataReader> ReadAllAsync(string table, CancellationToken ct)
    {
        var command = Source.CreateCommand();
        command.CommandText = $"SELECT * FROM {Q(table)}";
        return await command.ExecuteReaderAsync(ct);
    }

    public static Dictionary<string, int> Ordinals(SqliteDataReader reader)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++) map[reader.GetName(i)] = i;
        return map;
    }

    public static string? Text(SqliteDataReader reader, Dictionary<string, int> ordinals, params string[] names)
    {
        foreach (var name in names)
        {
            if (!ordinals.TryGetValue(name, out var i) || reader.IsDBNull(i)) continue;
            var value = Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)?.Trim();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    public static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    public static string RowJson(SqliteDataReader reader)
    {
        var values = new Dictionary<string, object?>();
        for (var i = 0; i < reader.FieldCount; i++) values[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return JsonSerializer.Serialize(values);
    }

    public async Task ExecuteAsync(string sql, IReadOnlyDictionary<string, object?> values, SqliteTransaction tx, CancellationToken ct)
    {
        await using var cmd = Target.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var item in values) cmd.Parameters.AddWithValue(item.Key, item.Value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ReviewAsync(string module, string table, string key, string reason)
    {
        static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
        await Review.WriteLineAsync($"{Csv(module)};{Csv(table)};{Csv(key)};{Csv(reason)}");
    }

    public static string Q(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}

public sealed class BuilderEngine
{
    private readonly string _source;
    private readonly string _output;
    private readonly IReadOnlyList<IBuilderModule> _modules;

    public BuilderEngine(string source, string output, IReadOnlyList<IBuilderModule> modules)
        => (_source, _output, _modules) = (source, output, modules);

    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_output);
        var final = Path.Combine(_output, "NovoERP_V4.sqlite");
        var partial = final + ".partial";
        var logPath = Path.Combine(_output, "BUILDER_V4.log");
        var reportPath = Path.Combine(_output, "RELATORIO_BUILDER_V4.json");
        var validationPath = Path.Combine(_output, "VALIDACAO_FINAL.md");
        var reviewPath = Path.Combine(_output, "REVISAO_V4.csv");
        var report = new BuildReport { StartedAt = DateTimeOffset.Now, Source = _source, Output = final };
        var watch = Stopwatch.StartNew();

        await using var log = new StreamWriter(logPath, false, new UTF8Encoding(true)) { AutoFlush = true };
        async Task WriteLog(string code, string message) => await log.WriteLineAsync($"{DateTimeOffset.Now:O} [{code}] {message}");

        try
        {
            if (!File.Exists(_source)) throw new FileNotFoundException("erp_intermediario.sqlite não encontrado.", _source);
            if (File.Exists(partial)) File.Delete(partial);
            if (File.Exists(final)) File.Delete(final);

            await using var source = new SqliteConnection($"Data Source={_source};Mode=ReadOnly;Cache=Private;Pooling=False");
            await using var target = new SqliteConnection($"Data Source={partial};Mode=ReadWriteCreate;Cache=Private;Pooling=False");
            await source.OpenAsync(ct);
            await target.OpenAsync(ct);
            await Exec(source, "PRAGMA query_only=ON; PRAGMA temp_store=MEMORY;", ct);
            await Exec(target, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=OFF;", ct);
            await CreateSchema(target, ct);

            var tables = await ReadTables(source, ct);
            await using var review = new StreamWriter(reviewPath, false, new UTF8Encoding(true)) { AutoFlush = true };
            await review.WriteLineAsync("modulo;tabela;chave;motivo");
            var context = new BuilderContext { Source = source, Target = target, SourceTables = tables, Review = review, LogAsync = WriteLog };

            foreach (var module in _modules)
            {
                Console.WriteLine($"[{module.Name}] iniciando...");
                var sw = Stopwatch.StartNew();
                var result = await module.ExecuteAsync(context, ct);
                result.Duration = sw.Elapsed.ToString("c");
                report.Modules[module.Name] = result;
                await WriteLog("MODULE_DONE", $"{module.Name}: lidos {result.Read:N0}; gravados {result.Written:N0}; revisão {result.Review:N0}");
            }

            await Validate(source, target, report, context, ct);
            await CreateIndexes(target, ct);
            await Exec(target, "PRAGMA optimize; PRAGMA wal_checkpoint(TRUNCATE);", ct);
            await target.CloseAsync();
            await source.CloseAsync();
            SqliteConnection.ClearAllPools();
            await MoveWithRetry(partial, final, ct);

            report.Success = true;
            report.FinishedAt = DateTimeOffset.Now;
            report.Duration = watch.Elapsed.ToString("c");
            report.Confidence = CalculateConfidence(report);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), ct);
            await File.WriteAllTextAsync(validationPath, BuildValidationMarkdown(report), ct);
            Console.WriteLine($"BUILDER V4.1 CONCLUÍDO: {final}");
            Console.WriteLine($"Confiabilidade: {report.Confidence:F2}%");
            return 0;
        }
        catch (Exception ex)
        {
            report.Success = false;
            report.FatalError = ex.ToString();
            report.FinishedAt = DateTimeOffset.Now;
            report.Duration = watch.Elapsed.ToString("c");
            await WriteLog("FATAL", ex.ToString());
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), ct);
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task<HashSet<string>> ReadTables(SqliteConnection connection, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) set.Add(reader.GetString(0));
        return set;
    }

    private static async Task Exec(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task CreateSchema(SqliteConnection connection, CancellationToken ct)
    {
        const string sql = """
CREATE TABLE clientes(id TEXT PRIMARY KEY,nome TEXT,fantasia TEXT,tipo_pessoa TEXT,cpf_cnpj TEXT,ie TEXT,im TEXT,email TEXT,telefone TEXT,cep TEXT,endereco TEXT,numero TEXT,bairro TEXT,cidade TEXT,uf TEXT,limite_credito REAL,saldo REAL,data_cadastro TEXT,ultima_compra TEXT,ultima_venda TEXT,raw_json TEXT,complemento_pf_json TEXT,complemento_pj_json TEXT);
CREATE TABLE cliente_contatos(id INTEGER PRIMARY KEY AUTOINCREMENT,cliente_id TEXT,tipo TEXT,valor TEXT,nome TEXT,principal TEXT,raw_json TEXT);
CREATE TABLE cliente_enderecos(id INTEGER PRIMARY KEY AUTOINCREMENT,cliente_id TEXT,tipo TEXT,cep TEXT,endereco TEXT,numero TEXT,complemento TEXT,bairro TEXT,cidade TEXT,uf TEXT,principal TEXT,raw_json TEXT);
CREATE TABLE fornecedores(id TEXT PRIMARY KEY,nome TEXT,fantasia TEXT,cpf_cnpj TEXT,email TEXT,telefone TEXT,ativo TEXT,raw_json TEXT);
CREATE TABLE produtos(id TEXT PRIMARY KEY,comprod_id TEXT,codigo TEXT,codigo_original TEXT,codigo_fabricante TEXT,descricao TEXT,descricao_reduzida TEXT,marca_id TEXT,grupo_id TEXT,subgrupo_id TEXT,ncm TEXT,cest TEXT,ean TEXT,unidade TEXT,peso REAL,volume REAL,largura REAL,altura REAL,comprimento REAL,origem TEXT,ativo TEXT,raw_json TEXT);
CREATE TABLE produto_codigos(id INTEGER PRIMARY KEY AUTOINCREMENT,produto_id TEXT,codigo TEXT,tipo TEXT,raw_json TEXT);
CREATE TABLE produto_precos(id INTEGER PRIMARY KEY AUTOINCREMENT,produto_id TEXT,custo REAL,venda REAL,minimo REAL,maximo REAL,margem REAL,tabela_id TEXT,vigencia_inicio TEXT,vigencia_fim TEXT,historico INTEGER,raw_json TEXT);
CREATE TABLE produto_aplicacoes(id INTEGER PRIMARY KEY AUTOINCREMENT,produto_id TEXT,descricao TEXT,marca TEXT,modelo TEXT,motor TEXT,ano_inicial TEXT,ano_final TEXT,raw_json TEXT);
CREATE TABLE produto_fornecedores(id INTEGER PRIMARY KEY AUTOINCREMENT,produto_id TEXT,fornecedor_id TEXT,codigo_fornecedor TEXT,custo REAL,principal TEXT,raw_json TEXT);
CREATE TABLE produto_similares(id INTEGER PRIMARY KEY AUTOINCREMENT,produto_id TEXT,produto_similar_id TEXT,raw_json TEXT);
CREATE TABLE estoque(id INTEGER PRIMARY KEY AUTOINCREMENT,produto_id TEXT,empresa_id TEXT,deposito_id TEXT,quantidade_fisica REAL,quantidade_reservada REAL,quantidade_disponivel REAL,quantidade_transito REAL,quantidade_compra REAL,quantidade_venda REAL,custo_medio REAL,ultimo_custo REAL,custo_reposicao REAL,custo_financeiro REAL,ultima_compra TEXT,ultima_venda TEXT,raw_json TEXT);
CREATE TABLE localizacoes(id INTEGER PRIMARY KEY AUTOINCREMENT,produto_id TEXT,deposito_id TEXT,rua TEXT,prateleira TEXT,coluna TEXT,nivel TEXT,posicao TEXT,raw_json TEXT);
CREATE TABLE estoque_orfaos(id INTEGER PRIMARY KEY AUTOINCREMENT,produto_id_origem TEXT,deposito_id TEXT,motivo TEXT,raw_json TEXT);
""";
        await Exec(connection, sql, ct);
    }

    private static async Task CreateIndexes(SqliteConnection connection, CancellationToken ct)
    {
        const string sql = """
CREATE INDEX idx_clientes_documento ON clientes(cpf_cnpj);
CREATE INDEX idx_produtos_codigo ON produtos(codigo);
CREATE INDEX idx_produtos_comprod ON produtos(comprod_id);
CREATE INDEX idx_estoque_produto ON estoque(produto_id);
CREATE INDEX idx_precos_produto ON produto_precos(produto_id);
CREATE INDEX idx_aplicacoes_produto ON produto_aplicacoes(produto_id);
CREATE INDEX idx_fornecedor_produto ON produto_fornecedores(produto_id);
""";
        await Exec(connection, sql, ct);
    }

    private static async Task Validate(SqliteConnection source, SqliteConnection target, BuildReport report, BuilderContext context, CancellationToken ct)
    {
        foreach (var table in new[] { "clientes", "cliente_contatos", "cliente_enderecos", "fornecedores", "produtos", "produto_codigos", "produto_precos", "produto_aplicacoes", "produto_fornecedores", "produto_similares", "estoque", "localizacoes", "estoque_orfaos" })
            report.TargetCounts[table] = await Count(target, $"SELECT COUNT(*) FROM {table}", ct);

        var sourceMap = new Dictionary<string, string>
        {
            ["clientes"] = "T_RRPARTS__PAR_ENTIDADES",
            ["produtos"] = "T_RRPARTS__COM_PRODUTOS",
            ["estoque"] = "T_RRPARTS__PCI_ITEM_DEPOSITO",
            ["fornecedores"] = context.HasTable("T_RRPARTS__PAR_FORN_ENTIDADE") ? "T_RRPARTS__PAR_FORN_ENTIDADE" : "T_RRPARTS__PAR_FORNECEDOR"
        };
        foreach (var item in sourceMap)
            if (context.HasTable(item.Value)) report.SourceCounts[item.Key] = await Count(source, $"SELECT COUNT(*) FROM {BuilderContext.Q(item.Value)}", ct);

        report.Validation["clientes_sem_nome"] = await Count(target, "SELECT COUNT(*) FROM clientes WHERE COALESCE(TRIM(nome),'')=''", ct);
        report.Validation["clientes_sem_documento"] = await Count(target, "SELECT COUNT(*) FROM clientes WHERE COALESCE(TRIM(cpf_cnpj),'')=''", ct);
        report.Validation["produtos_sem_codigo"] = await Count(target, "SELECT COUNT(*) FROM produtos WHERE COALESCE(TRIM(codigo),'')=''", ct);
        report.Validation["produtos_sem_descricao"] = await Count(target, "SELECT COUNT(*) FROM produtos WHERE COALESCE(TRIM(descricao),'')=''", ct);
        report.Validation["estoque_sem_produto"] = await Count(target, "SELECT COUNT(*) FROM estoque e LEFT JOIN produtos p ON p.id=e.produto_id WHERE p.id IS NULL", ct);
        report.Validation["precos_sem_produto"] = await Count(target, "SELECT COUNT(*) FROM produto_precos pp LEFT JOIN produtos p ON p.comprod_id=pp.produto_id OR p.id=pp.produto_id WHERE p.id IS NULL", ct);
        report.Validation["fornecedores_sem_entidade"] = await Count(target, "SELECT COUNT(*) FROM fornecedores f LEFT JOIN clientes c ON c.id=f.id WHERE c.id IS NULL", ct);

        await using var orphanCmd = target.CreateCommand();
        orphanCmd.CommandText = "SELECT produto_id, deposito_id, raw_json FROM estoque e LEFT JOIN produtos p ON p.id=e.produto_id WHERE p.id IS NULL";
        await using var orphanReader = await orphanCmd.ExecuteReaderAsync(ct);
        while (await orphanReader.ReadAsync(ct))
            await context.ReviewAsync("ESTOQUE", "T_RRPARTS__PCI_ITEM_DEPOSITO", orphanReader.IsDBNull(0) ? "" : orphanReader.GetString(0), "Produto não encontrado no cadastro principal");
    }

    private static async Task<long> Count(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static double CalculateConfidence(BuildReport report)
    {
        var expected = report.SourceCounts.Values.Sum();
        if (expected <= 0) return 0;
        var matched = 0L;
        foreach (var key in new[] { "clientes", "produtos", "estoque", "fornecedores" })
            if (report.SourceCounts.TryGetValue(key, out var source) && report.TargetCounts.TryGetValue(key, out var target)) matched += Math.Min(source, target);
        var qualityPenalty = report.Validation.Values.Sum();
        return Math.Clamp((matched - qualityPenalty) / (double)expected * 100d, 0d, 100d);
    }

    private static string BuildValidationMarkdown(BuildReport report)
    {
        var sb = new StringBuilder("# Validação final da migração\n\n");
        sb.AppendLine($"- Sucesso: **{report.Success}**");
        sb.AppendLine($"- Confiabilidade estimada: **{report.Confidence:F2}%**");
        sb.AppendLine($"- Duração: **{report.Duration}**\n");
        sb.AppendLine("## Comparação de totais\n");
        sb.AppendLine("| Módulo | ERP antigo | ERP novo | Diferença | Status |");
        sb.AppendLine("|---|---:|---:|---:|---|");
        foreach (var key in new[] { "clientes", "produtos", "estoque", "fornecedores" })
        {
            report.SourceCounts.TryGetValue(key, out var source);
            report.TargetCounts.TryGetValue(key, out var target);
            var difference = target - source;
            sb.AppendLine($"| {key.ToUpperInvariant()} | {source:N0} | {target:N0} | {difference:N0} | {(difference == 0 ? "OK" : "REVISAR")} |");
        }
        sb.AppendLine("\n## Validações de qualidade\n");
        foreach (var item in report.Validation) sb.AppendLine($"- **{item.Key}**: {item.Value:N0}");
        return sb.ToString();
    }

    private static async Task MoveWithRetry(string source, string destination, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 15; attempt++)
        {
            try { File.Move(source, destination, true); return; }
            catch (IOException ex) { last = ex; await Task.Delay(attempt * 200, ct); }
        }
        throw new IOException("Não foi possível finalizar o banco SQLite.", last);
    }
}

public sealed class ModuleResult
{
    public long Read { get; set; }
    public long Written { get; set; }
    public long Review { get; set; }
    public string? Duration { get; set; }
}

public sealed class BuildReport
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public string Source { get; set; } = "";
    public string Output { get; set; } = "";
    public string Duration { get; set; } = "";
    public bool Success { get; set; }
    public string? FatalError { get; set; }
    public double Confidence { get; set; }
    public Dictionary<string, ModuleResult> Modules { get; set; } = new();
    public Dictionary<string, long> SourceCounts { get; set; } = new();
    public Dictionary<string, long> TargetCounts { get; set; } = new();
    public Dictionary<string, long> Validation { get; set; } = new();
}
