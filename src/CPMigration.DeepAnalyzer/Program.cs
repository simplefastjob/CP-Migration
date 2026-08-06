using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using CPMigration.DeepAnalyzer;

Console.OutputEncoding = Encoding.UTF8;
var root = Directory.GetCurrentDirectory();
var database = GetArg("--database") ?? Path.Combine(root, "output", "erp_intermediario.sqlite");
var output = GetArg("--output") ?? Path.Combine(root, "AnaliseCompleta");
Directory.CreateDirectory(output);

var logPath = Path.Combine(output, "LOG_COMPLETO.txt");
await using var log = new StreamWriter(logPath, false, new UTF8Encoding(true)) { AutoFlush = true };
var watch = Stopwatch.StartNew();

try
{
    if (!File.Exists(database)) throw new FileNotFoundException("Banco intermediário não encontrado.", database);
    await WriteLogAsync("INFO", "START", $"Iniciando análise profunda: {database}");

    var analysis = new DatabaseAnalysis
    {
        DatabasePath = Path.GetFullPath(database),
        DatabaseSizeBytes = new FileInfo(database).Length
    };

    await using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Cache=Private;Pooling=False");
    await connection.OpenAsync();
    await ExecuteAsync(connection, "PRAGMA query_only=ON; PRAGMA temp_store=MEMORY;");

    var tables = await ReadTablesAsync(connection);
    await WriteLogAsync("INFO", "TABLES_FOUND", $"Tabelas encontradas: {tables.Count:N0}");

    for (var i = 0; i < tables.Count; i++)
    {
        var table = tables[i];
        Console.WriteLine($"[{i + 1:N0}/{tables.Count:N0}] {table.Name}");
        await WriteLogAsync("INFO", "TABLE_START", $"[{i + 1}/{tables.Count}] Analisando {table.Name}");

        try
        {
            table.RowCount = await ScalarLongAsync(connection, $"SELECT COUNT(*) FROM {Quote(table.Name)}");
            table.Columns.AddRange(await ReadColumnsAsync(connection, table.Name));
            table.ForeignKeys.AddRange(await ReadForeignKeysAsync(connection, table.Name));
            table.Indexes.AddRange(await ReadIndexesAsync(connection, table.Name));
            ClassifyDomain(table);

            foreach (var column in table.Columns)
            {
                try
                {
                    await ProfileColumnAsync(connection, table, column);
                    ClassifyMeaning(column);
                }
                catch (Exception ex)
                {
                    analysis.Issues.Add(new AnalysisIssue("WARNING", "COLUMN_PROFILE_FAILED", ex.Message, table.Name, column.Name));
                    await WriteLogAsync("WARNING", "COLUMN_PROFILE_FAILED", $"{table.Name}.{column.Name}: {ex.Message}");
                }
            }

            analysis.Tables.Add(table);
            await WriteLogAsync("INFO", "TABLE_DONE", $"{table.Name}: {table.RowCount:N0} registros; {table.Columns.Count} colunas; domínio {table.SuggestedDomain} ({table.DomainConfidence:P0})");
        }
        catch (Exception ex)
        {
            analysis.Issues.Add(new AnalysisIssue("ERROR", "TABLE_ANALYSIS_FAILED", ex.Message, table.Name));
            await WriteLogAsync("ERROR", "TABLE_ANALYSIS_FAILED", $"{table.Name}: {ex}");
        }
    }

    await WriteOutputsAsync(analysis, output);
    watch.Stop();
    await WriteLogAsync("INFO", "DONE", $"Análise concluída em {watch.Elapsed:hh\\:mm\\:ss}. Tabelas: {analysis.TableCount:N0}; registros: {analysis.TotalRows:N0}; problemas: {analysis.Issues.Count:N0}");

    Console.WriteLine();
    Console.WriteLine("ANÁLISE CONCLUÍDA");
    Console.WriteLine($"Pasta: {output}");
    Environment.ExitCode = 0;
}
catch (Exception ex)
{
    await WriteLogAsync("FATAL", "ANALYSIS_FATAL", ex.ToString());
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}

async Task<List<TableAnalysis>> ReadTablesAsync(SqliteConnection connection)
{
    var result = new List<TableAnalysis>();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT name, COALESCE(sql,'') FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) result.Add(new TableAnalysis { Name = reader.GetString(0), Sql = reader.GetString(1) });
    return result;
}

async Task<List<ColumnAnalysis>> ReadColumnsAsync(SqliteConnection connection, string table)
{
    var result = new List<ColumnAnalysis>();
    await using var command = connection.CreateCommand();
    command.CommandText = $"PRAGMA table_info({Quote(table)})";
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        result.Add(new ColumnAnalysis
        {
            Ordinal = reader.GetInt32(0),
            Name = reader.GetString(1),
            DeclaredType = reader.IsDBNull(2) ? "" : reader.GetString(2),
            NotNull = reader.GetInt32(3) == 1,
            DefaultValue = reader.IsDBNull(4) ? null : reader.GetValue(4)?.ToString(),
            PrimaryKey = reader.GetInt32(5) > 0
        });
    }
    return result;
}

async Task<List<ForeignKeyAnalysis>> ReadForeignKeysAsync(SqliteConnection connection, string table)
{
    var result = new List<ForeignKeyAnalysis>();
    await using var command = connection.CreateCommand();
    command.CommandText = $"PRAGMA foreign_key_list({Quote(table)})";
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) result.Add(new ForeignKeyAnalysis(reader.GetString(3), reader.GetString(2), reader.GetString(4), reader.GetString(5), reader.GetString(6)));
    return result;
}

async Task<List<IndexAnalysis>> ReadIndexesAsync(SqliteConnection connection, string table)
{
    var result = new List<IndexAnalysis>();
    await using var command = connection.CreateCommand();
    command.CommandText = $"PRAGMA index_list({Quote(table)})";
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var name = reader.GetString(1);
        var unique = reader.GetInt32(2) == 1;
        var columns = new List<string>();
        await using var detail = connection.CreateCommand();
        detail.CommandText = $"PRAGMA index_info({Quote(name)})";
        await using var detailReader = await detail.ExecuteReaderAsync();
        while (await detailReader.ReadAsync()) columns.Add(detailReader.GetString(2));
        result.Add(new IndexAnalysis(name, unique, columns));
    }
    return result;
}

async Task ProfileColumnAsync(SqliteConnection connection, TableAnalysis table, ColumnAnalysis column)
{
    var qTable = Quote(table.Name);
    var qColumn = Quote(column.Name);
    await using (var command = connection.CreateCommand())
    {
        command.CommandText = $"SELECT COUNT({qColumn}), COUNT(DISTINCT {qColumn}), COALESCE(MAX(LENGTH(CAST({qColumn} AS TEXT))),0) FROM {qTable}";
        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            column.NonNullCount = reader.GetInt64(0);
            column.DistinctCount = reader.GetInt64(1);
            column.MaxLength = reader.GetInt32(2);
        }
    }

    await using (var command = connection.CreateCommand())
    {
        command.CommandText = $"SELECT CAST({qColumn} AS TEXT), COUNT(*) AS qtd FROM {qTable} WHERE {qColumn} IS NOT NULL AND TRIM(CAST({qColumn} AS TEXT)) <> '' GROUP BY CAST({qColumn} AS TEXT) ORDER BY qtd DESC LIMIT 8";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) column.Examples.Add(reader.GetString(0));
    }
}

void ClassifyDomain(TableAnalysis table)
{
    var name = table.Name.ToUpperInvariant();
    var groups = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["CLIENTES-ENTIDADES"] = ["ENTIDADE", "CLIENTE", "PESSOA", "CONTATO", "ENDERECO", "TELEFONE"],
        ["PRODUTOS-CATALOGO"] = ["PRODUTO", "PROD", "MARCA", "APLICACAO", "EQUIVAL", "CATALOGO"],
        ["ESTOQUE-LOGISTICA"] = ["ESTOQUE", "DEPOSITO", "LOCACAO", "INVENTARIO", "KARDEX", "ALOCACAO"],
        ["COMPRAS-SUPRIMENTOS"] = ["COMPRA", "COTACAO", "FORNECEDOR", "SUPRIMENTO"],
        ["VENDAS-COMERCIAL"] = ["VENDA", "PEDIDO", "ORCAMENTO", "FATURAMENTO"],
        ["FINANCEIRO"] = ["FIN_", "TITULO", "PAGAMENTO", "RECEBER", "PAGAR", "CAIXA", "BANCO"],
        ["FISCAL-TRIBUTARIO"] = ["FISCAL", "NOTA", "NFE", "NFCE", "CTE", "TRIBUT", "ICMS", "IPI"],
        ["CONTABILIDADE"] = ["CTB_", "CONTABIL", "PLANO_CONTA", "LANCAMENTO"],
        ["GARANTIAS-DEVOLUCOES"] = ["GARANTIA", "DEVOLU", "TROCA"],
        ["USUARIOS-PERMISSOES"] = ["USUARIO", "PERMISSAO", "ACESSO", "LOGIN"]
    };

    var best = groups.Select(g => new { Domain = g.Key, Hits = g.Value.Count(name.Contains) }).OrderByDescending(x => x.Hits).First();
    if (best.Hits > 0)
    {
        table.SuggestedDomain = best.Domain;
        table.DomainConfidence = Math.Min(.95, .55 + best.Hits * .12);
    }
}

void ClassifyMeaning(ColumnAnalysis column)
{
    var name = Normalize(column.Name);
    var examples = column.Examples;
    var candidates = new List<(string Meaning, double Score)>();
    AddNameScore("ID", ["ID", "CODIGO", "CODG"], .65);
    AddNameScore("NOME", ["NOME", "RAZAO", "FANTASIA", "DESCRICAO", "DESC"], .70);
    AddNameScore("CPF_CNPJ", ["CPF", "CNPJ", "CGC", "DOCUMENTO"], .80);
    AddNameScore("EMAIL", ["EMAIL", "MAIL"], .90);
    AddNameScore("TELEFONE", ["TELEFONE", "FONE", "CELULAR"], .85);
    AddNameScore("CEP", ["CEP"], .95);
    AddNameScore("DATA", ["DATA", "DTHR", "DT_", "DATE"], .75);
    AddNameScore("VALOR", ["VALOR", "VALR", "PRECO", "CUSTO", "TOTAL"], .72);
    AddNameScore("QUANTIDADE", ["QUANT", "QTDE", "QTD", "SALDO"], .72);
    AddNameScore("STATUS", ["STATUS", "SITUACAO", "ATIVO", "INDR"], .68);

    if (examples.Count > 0)
    {
        var emailRate = examples.Count(x => Regex.IsMatch(x, @"^[^\s@]+@[^\s@]+\.[^\s@]+$")) / (double)examples.Count;
        var digitsRate = examples.Count(x => Regex.IsMatch(Regex.Replace(x, "\\D", ""), @"^\d{11,14}$")) / (double)examples.Count;
        if (emailRate >= .5) candidates.Add(("EMAIL", .95));
        if (digitsRate >= .7 && (name.Contains("CPF") || name.Contains("CNPJ") || name.Contains("CGC"))) candidates.Add(("CPF_CNPJ", .96));
    }

    var best = candidates.OrderByDescending(x => x.Score).FirstOrDefault();
    if (best.Score > 0)
    {
        column.SuggestedMeaning = best.Meaning;
        column.MeaningConfidence = best.Score;
    }

    void AddNameScore(string meaning, string[] terms, double score)
    {
        if (terms.Any(name.Contains)) candidates.Add((meaning, score));
    }
}

async Task WriteOutputsAsync(DatabaseAnalysis analysis, string directory)
{
    var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
    await File.WriteAllTextAsync(Path.Combine(directory, "EstruturaCompleta.json"), JsonSerializer.Serialize(analysis, jsonOptions), Encoding.UTF8);

    var tablesMd = new StringBuilder("# Todas as tabelas\n\n| Tabela | Registros | Colunas | Domínio sugerido | Confiança |\n|---|---:|---:|---|---:|\n");
    foreach (var table in analysis.Tables.OrderByDescending(x => x.RowCount))
        tablesMd.AppendLine($"| `{table.Name}` | {table.RowCount:N0} | {table.Columns.Count:N0} | {table.SuggestedDomain} | {table.DomainConfidence:P0} |");
    await File.WriteAllTextAsync(Path.Combine(directory, "TodasAsTabelas.md"), tablesMd.ToString(), Encoding.UTF8);

    var columnsMd = new StringBuilder("# Todas as colunas\n\n");
    foreach (var table in analysis.Tables)
    {
        columnsMd.AppendLine($"## {table.Name}\n");
        columnsMd.AppendLine("| Coluna | Tipo | PK | Preenchidos | Distintos | Significado | Confiança | Exemplos |");
        columnsMd.AppendLine("|---|---|---:|---:|---:|---|---:|---|");
        foreach (var column in table.Columns)
            columnsMd.AppendLine($"| `{column.Name}` | {column.DeclaredType} | {(column.PrimaryKey ? "Sim" : "Não")} | {column.NonNullCount:N0} | {column.DistinctCount:N0} | {column.SuggestedMeaning} | {column.MeaningConfidence:P0} | {string.Join(" · ", column.Examples.Take(3)).Replace("|", "\\|")} |");
        columnsMd.AppendLine();
    }
    await File.WriteAllTextAsync(Path.Combine(directory, "TodasAsColunas.md"), columnsMd.ToString(), Encoding.UTF8);

    var html = new StringBuilder();
    html.Append("<!doctype html><html lang='pt-BR'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width'><title>CP Migration - Análise Completa</title><style>body{font-family:Segoe UI;background:#0d121c;color:#edf4ff;margin:0}header{padding:28px;background:#171e2c}main{padding:24px}.cards{display:flex;gap:16px;flex-wrap:wrap}.card{background:#20293a;padding:18px;border-radius:12px;min-width:180px}table{width:100%;border-collapse:collapse;margin-top:20px;background:#171e2c}th,td{padding:10px;border-bottom:1px solid #303b50;text-align:left}input{width:100%;padding:12px;background:#20293a;color:white;border:1px solid #3a4760;border-radius:8px}</style></head><body>");
    html.Append($"<header><h1>CP Migration — Análise Completa</h1><p>{WebUtility.HtmlEncode(analysis.DatabasePath)}</p></header><main><div class='cards'><div class='card'><b>Tabelas</b><div>{analysis.TableCount:N0}</div></div><div class='card'><b>Registros</b><div>{analysis.TotalRows:N0}</div></div><div class='card'><b>Problemas</b><div>{analysis.Issues.Count:N0}</div></div></div><p><input id='q' placeholder='Filtrar tabela...' oninput='filterRows()'></p><table><thead><tr><th>Tabela</th><th>Registros</th><th>Colunas</th><th>Domínio</th><th>Confiança</th></tr></thead><tbody id='rows'>");
    foreach (var table in analysis.Tables.OrderByDescending(x => x.RowCount)) html.Append($"<tr><td>{WebUtility.HtmlEncode(table.Name)}</td><td>{table.RowCount:N0}</td><td>{table.Columns.Count}</td><td>{table.SuggestedDomain}</td><td>{table.DomainConfidence:P0}</td></tr>");
    html.Append("</tbody></table></main><script>function filterRows(){const q=document.getElementById('q').value.toLowerCase();document.querySelectorAll('#rows tr').forEach(r=>r.style.display=r.innerText.toLowerCase().includes(q)?'':'none')}</script></body></html>");
    await File.WriteAllTextAsync(Path.Combine(directory, "index.html"), html.ToString(), Encoding.UTF8);
}

async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
{
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    return Convert.ToInt64(await command.ExecuteScalarAsync());
}

async Task ExecuteAsync(SqliteConnection connection, string sql)
{
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    await command.ExecuteNonQueryAsync();
}

async Task WriteLogAsync(string level, string code, string message)
{
    var line = $"{DateTimeOffset.Now:O} [{level}] {code} {message}";
    await log.WriteLineAsync(line);
}

string? GetArg(string name)
{
    var index = Array.FindIndex(args, x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : null;
}

static string Normalize(string value) => Regex.Replace(value.ToUpperInvariant().Normalize(NormalizationForm.FormD), "[^A-Z0-9_]", "");
static string Quote(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
