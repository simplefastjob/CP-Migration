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
    public required Func<string,string,Task> LogAsync { get; init; }

    public bool HasTable(string name) => SourceTables.Contains(name);

    public async Task<SqliteDataReader> ReadAllAsync(string table, CancellationToken ct)
    {
        var command = Source.CreateCommand();
        command.CommandText = $"SELECT * FROM {Q(table)}";
        return await command.ExecuteReaderAsync(ct);
    }

    public static Dictionary<string,int> Ordinals(SqliteDataReader reader)
    {
        var map = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++) map[reader.GetName(i)] = i;
        return map;
    }

    public static string? Text(SqliteDataReader reader, Dictionary<string,int> ordinals, params string[] names)
    {
        foreach (var name in names)
            if (ordinals.TryGetValue(name, out var i) && !reader.IsDBNull(i))
            {
                var value = Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)?.Trim();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        return null;
    }

    public static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    public static string RowJson(SqliteDataReader reader)
    {
        var values = new Dictionary<string,object?>();
        for (var i = 0; i < reader.FieldCount; i++) values[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return JsonSerializer.Serialize(values);
    }

    public async Task ExecuteAsync(string sql, IReadOnlyDictionary<string,object?> values, SqliteTransaction tx, CancellationToken ct)
    {
        await using var cmd = Target.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var item in values) cmd.Parameters.AddWithValue(item.Key, item.Value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ReviewAsync(string module, string table, string key, string reason)
    {
        static string C(string s) => '"' + s.Replace("\"", "\"\"") + '"';
        await Review.WriteLineAsync($"{C(module)};{C(table)};{C(key)};{C(reason)}");
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

            await Validate(target, report, ct);
            await Exec(target, "PRAGMA optimize; PRAGMA wal_checkpoint(TRUNCATE);", ct);
            await target.CloseAsync();
            await source.CloseAsync();
            SqliteConnection.ClearAllPools();
            File.Move(partial, final, true);

            report.Success = true;
            report.FinishedAt = DateTimeOffset.Now;
            report.Duration = watch.Elapsed.ToString("c");
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), ct);
            Console.WriteLine($"BUILDER V4 CONCLUÍDO: {final}");
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

    private static async Task<HashSet<string>> ReadTables(SqliteConnection c, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) set.Add(r.GetString(0));
        return set;
    }

    private static async Task Exec(SqliteConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task CreateSchema(SqliteConnection c, CancellationToken ct)
    {
        const string sql = """
CREATE TABLE clientes(id TEXT PRIMARY KEY,nome TEXT,fantasia TEXT,tipo_pessoa TEXT,cpf_cnpj TEXT,ie TEXT,im TEXT,email TEXT,telefone TEXT,cep TEXT,endereco TEXT,numero TEXT,bairro TEXT,cidade TEXT,uf TEXT,limite_credito REAL,saldo REAL,data_cadastro TEXT,ultima_compra TEXT,ultima_venda TEXT,raw_json TEXT);
CREATE TABLE fornecedores(id TEXT PRIMARY KEY,nome TEXT,cpf_cnpj TEXT,email TEXT,telefone TEXT,raw_json TEXT);
CREATE TABLE produtos(id TEXT PRIMARY KEY,comprod_id TEXT,codigo TEXT,codigo_original TEXT,codigo_fabricante TEXT,descricao TEXT,descricao_reduzida TEXT,marca_id TEXT,grupo_id TEXT,subgrupo_id TEXT,ncm TEXT,cest TEXT,ean TEXT,unidade TEXT,peso REAL,origem TEXT,ativo TEXT,raw_json TEXT);
CREATE TABLE produto_codigos(id INTEGER PRIMARY KEY AUTOINCREMENT,produto_id TEXT,codigo TEXT,tipo TEXT,raw_json TEXT);
CREATE TABLE produto_precos(id INTEGER PRIMARY KEY AUTOINCREMENT,produto_id TEXT,custo REAL,venda REAL,minimo REAL,maximo REAL,margem REAL,tabela_id TEXT,vigencia_inicio TEXT,vigencia_fim TEXT,historico INTEGER,raw_json TEXT);
CREATE TABLE produto_aplicacoes(id INTEGER PRIMARY KEY AUTOINCREMENT,produto_id TEXT,descricao TEXT,marca TEXT,modelo TEXT,motor TEXT,ano_inicial TEXT,ano_final TEXT,raw_json TEXT);
CREATE TABLE produto_fornecedores(id INTEGER PRIMARY KEY AUTOINCREMENT,produto_id TEXT,fornecedor_id TEXT,codigo_fornecedor TEXT,custo REAL,principal TEXT,raw_json TEXT);
CREATE TABLE produto_similares(id INTEGER PRIMARY KEY AUTOINCREMENT,produto_id TEXT,produto_similar_id TEXT,raw_json TEXT);
CREATE TABLE estoque(id INTEGER PRIMARY KEY AUTOINCREMENT,produto_id TEXT,empresa_id TEXT,deposito_id TEXT,quantidade_fisica REAL,quantidade_reservada REAL,quantidade_disponivel REAL,custo_medio REAL,ultimo_custo REAL,ultima_compra TEXT,ultima_venda TEXT,raw_json TEXT);
CREATE TABLE localizacoes(id INTEGER PRIMARY KEY AUTOINCREMENT,produto_id TEXT,deposito_id TEXT,rua TEXT,prateleira TEXT,coluna TEXT,nivel TEXT,posicao TEXT,raw_json TEXT);
""";
        await Exec(c, sql, ct);
    }

    private static async Task Validate(SqliteConnection c, BuildReport report, CancellationToken ct)
    {
        foreach (var table in new[] { "clientes","fornecedores","produtos","produto_codigos","produto_precos","produto_aplicacoes","produto_fornecedores","produto_similares","estoque","localizacoes" })
        {
            await using var cmd = c.CreateCommand(); cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            report.TargetCounts[table] = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }
        report.Validation["clientes_sem_nome"] = await Count(c,"SELECT COUNT(*) FROM clientes WHERE COALESCE(TRIM(nome),'')=''",ct);
        report.Validation["produtos_sem_codigo"] = await Count(c,"SELECT COUNT(*) FROM produtos WHERE COALESCE(TRIM(codigo),'')=''",ct);
        report.Validation["estoque_sem_produto"] = await Count(c,"SELECT COUNT(*) FROM estoque e LEFT JOIN produtos p ON p.id=e.produto_id WHERE p.id IS NULL",ct);
    }

    private static async Task<long> Count(SqliteConnection c, string sql, CancellationToken ct)
    { await using var cmd=c.CreateCommand(); cmd.CommandText=sql; return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)); }
}

public sealed class ModuleResult { public long Read { get; set; } public long Written { get; set; } public long Review { get; set; } public string? Duration { get; set; } }
public sealed class BuildReport
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public string Source { get; set; } = "";
    public string Output { get; set; } = "";
    public string Duration { get; set; } = "";
    public bool Success { get; set; }
    public string? FatalError { get; set; }
    public Dictionary<string,ModuleResult> Modules { get; set; } = new();
    public Dictionary<string,long> TargetCounts { get; set; } = new();
    public Dictionary<string,long> Validation { get; set; } = new();
}
