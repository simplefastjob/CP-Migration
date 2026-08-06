using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

Console.OutputEncoding = Encoding.UTF8;
var root = Directory.GetCurrentDirectory();
var database = Arg("--database") ?? Path.Combine(root, "output", "erp_intermediario.sqlite");
var structurePath = Arg("--structure") ?? Path.Combine(root, "AnaliseCompleta", "EstruturaCompleta.json");
var output = Arg("--output") ?? Path.Combine(root, "AnaliseCompleta");
Directory.CreateDirectory(output);
var logPath = Path.Combine(output, "LOG_RELACIONAMENTOS.txt");
await using var log = new StreamWriter(logPath, false, new UTF8Encoding(true)) { AutoFlush = true };

try
{
    if (!File.Exists(database)) throw new FileNotFoundException("Banco intermediário não encontrado.", database);
    if (!File.Exists(structurePath)) throw new FileNotFoundException("EstruturaCompleta.json não encontrado. Execute a Parte 1 primeiro.", structurePath);

    await Log("START", $"Banco: {database}");
    var structure = JsonSerializer.Deserialize<DatabaseStructure>(await File.ReadAllTextAsync(structurePath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("Não foi possível ler EstruturaCompleta.json.");

    var tables = structure.Tables.Where(t => t.RowCount > 0).OrderByDescending(t => t.RowCount).ToArray();
    await Log("TABLES", $"Tabelas com dados: {tables.Length:N0} de {structure.Tables.Count:N0}");

    await using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Cache=Private;Pooling=False");
    await connection.OpenAsync();
    await Exec(connection, "PRAGMA query_only=ON; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-65536;");

    var candidates = BuildCandidates(tables);
    await Log("CANDIDATES", $"Pares candidatos por nome: {candidates.Count:N0}");

    var relationships = new List<RelationshipResult>();
    for (var i = 0; i < candidates.Count; i++)
    {
        var c = candidates[i];
        if (i % 100 == 0) Console.WriteLine($"Relacionamentos: {i:N0}/{candidates.Count:N0}");
        try
        {
            var result = await TestRelationship(connection, c);
            if (result.Confidence >= .55)
            {
                relationships.Add(result);
                await Log("RELATION", $"{result.ChildTable}.{result.ChildColumn} -> {result.ParentTable}.{result.ParentColumn} | confiança {result.Confidence:P0} | cobertura {result.Coverage:P0}");
            }
        }
        catch (Exception ex)
        {
            await Log("WARNING", $"{c.LeftTable}.{c.LeftColumn} x {c.RightTable}.{c.RightColumn}: {ex.Message}");
        }
    }

    relationships = relationships
        .GroupBy(r => $"{r.ChildTable}|{r.ChildColumn}|{r.ParentTable}|{r.ParentColumn}", StringComparer.OrdinalIgnoreCase)
        .Select(g => g.OrderByDescending(x => x.Confidence).First())
        .OrderByDescending(x => x.Confidence).ThenByDescending(x => x.Coverage).ToList();

    var ranking = tables.Select(t =>
    {
        var links = relationships.Count(r => r.ChildTable.Equals(t.Name, StringComparison.OrdinalIgnoreCase) || r.ParentTable.Equals(t.Name, StringComparison.OrdinalIgnoreCase));
        var meanings = t.Columns.Count(c => c.SuggestedMeaning != "DESCONHECIDO");
        var score = links * 100d + Math.Log10(Math.Max(1, t.RowCount)) * 20d + meanings * 3d;
        return new TableRanking(t.Name, t.RowCount, t.SuggestedDomain, links, meanings, score);
    }).OrderByDescending(x => x.Score).ToList();

    var domainMaps = tables.GroupBy(t => t.SuggestedDomain).ToDictionary(
        g => g.Key,
        g => g.OrderByDescending(t => ranking.First(r => r.Table == t.Name).Score).Take(30).Select(t => new DomainTable(
            t.Name,
            t.RowCount,
            t.DomainConfidence,
            t.Columns.Where(c => c.SuggestedMeaning != "DESCONHECIDO").Select(c => new FieldCandidate(c.Name, c.SuggestedMeaning, c.MeaningConfidence, c.NonNullCount, c.DistinctCount, c.Examples.Take(5).ToArray())).ToArray(),
            relationships.Where(r => r.ChildTable == t.Name || r.ParentTable == t.Name).Take(25).ToArray()
        )).ToArray());

    var resultObject = new AnalysisResult(DateTimeOffset.Now, database, tables.Length, relationships.Count, relationships, ranking, domainMaps);
    await File.WriteAllTextAsync(Path.Combine(output, "RelacionamentosDescobertos.json"), JsonSerializer.Serialize(resultObject, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    await File.WriteAllTextAsync(Path.Combine(output, "TOP_TABELAS.md"), BuildRankingMarkdown(ranking), Encoding.UTF8);
    await File.WriteAllTextAsync(Path.Combine(output, "MAPEAMENTO_INTELIGENTE.md"), BuildMappingMarkdown(domainMaps, relationships), Encoding.UTF8);
    await File.WriteAllTextAsync(Path.Combine(output, "Relacionamentos.csv"), BuildCsv(relationships), Encoding.UTF8);

    await Log("DONE", $"Relacionamentos aceitos: {relationships.Count:N0}");
    Console.WriteLine($"Concluído. Relacionamentos: {relationships.Count:N0}");
}
catch (Exception ex)
{
    await Log("FATAL", ex.ToString());
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}

List<RelationCandidate> BuildCandidates(TableInfo[] tables)
{
    var list = new List<RelationCandidate>();
    var byColumn = new Dictionary<string, List<(TableInfo Table, ColumnInfo Column)>>(StringComparer.OrdinalIgnoreCase);
    foreach (var table in tables)
    foreach (var column in table.Columns.Where(c => IsKeyLike(c.Name) && c.NonNullCount > 0 && c.DistinctCount > 0))
    {
        var normalized = NormalizeKey(column.Name);
        if (!byColumn.TryGetValue(normalized, out var bucket)) byColumn[normalized] = bucket = [];
        bucket.Add((table, column));
    }

    foreach (var bucket in byColumn.Values)
    {
        foreach (var a in bucket)
        foreach (var b in bucket)
        {
            if (string.Compare(a.Table.Name, b.Table.Name, StringComparison.OrdinalIgnoreCase) >= 0) continue;
            if (a.Table.RowCount == 0 || b.Table.RowCount == 0) continue;
            if (Math.Min(a.Column.DistinctCount, b.Column.DistinctCount) == 0) continue;
            list.Add(new RelationCandidate(a.Table.Name, a.Column.Name, a.Table.RowCount, a.Column.DistinctCount, b.Table.Name, b.Column.Name, b.Table.RowCount, b.Column.DistinctCount));
        }
    }
    return list.OrderByDescending(x => Math.Min(x.LeftDistinct, x.RightDistinct)).Take(120000).ToList();
}

async Task<RelationshipResult> TestRelationship(SqliteConnection connection, RelationCandidate c)
{
    var leftParentScore = ParentScore(c.LeftRows, c.LeftDistinct);
    var rightParentScore = ParentScore(c.RightRows, c.RightDistinct);
    var parentLeft = leftParentScore >= rightParentScore;
    var parentTable = parentLeft ? c.LeftTable : c.RightTable;
    var parentColumn = parentLeft ? c.LeftColumn : c.RightColumn;
    var childTable = parentLeft ? c.RightTable : c.LeftTable;
    var childColumn = parentLeft ? c.RightColumn : c.LeftColumn;
    var childRows = parentLeft ? c.RightRows : c.LeftRows;

    const int sampleLimit = 5000;
    await using var command = connection.CreateCommand();
    command.CommandTimeout = 120;
    command.CommandText = $"""
WITH sample AS (
  SELECT DISTINCT {Q(childColumn)} AS v
  FROM {Q(childTable)}
  WHERE {Q(childColumn)} IS NOT NULL AND TRIM(CAST({Q(childColumn)} AS TEXT)) <> ''
  LIMIT {sampleLimit}
)
SELECT COUNT(*) AS sampled,
       SUM(CASE WHEN EXISTS (SELECT 1 FROM {Q(parentTable)} p WHERE p.{Q(parentColumn)} = sample.v LIMIT 1) THEN 1 ELSE 0 END) AS matched
FROM sample;
""";
    await using var reader = await command.ExecuteReaderAsync();
    await reader.ReadAsync();
    var sampled = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
    var matched = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
    var coverage = sampled == 0 ? 0 : matched / (double)sampled;
    var nameScore = NormalizeKey(parentColumn).Equals(NormalizeKey(childColumn), StringComparison.OrdinalIgnoreCase) ? 1d : .6d;
    var uniqueness = parentLeft ? c.LeftDistinct / (double)Math.Max(1, c.LeftRows) : c.RightDistinct / (double)Math.Max(1, c.RightRows);
    var confidence = Math.Clamp(coverage * .72 + Math.Min(1, uniqueness) * .18 + nameScore * .10, 0, 1);
    return new RelationshipResult(childTable, childColumn, parentTable, parentColumn, sampled, matched, coverage, uniqueness, confidence, childRows);
}

double ParentScore(long rows, long distinct) => distinct / (double)Math.Max(1, rows);
bool IsKeyLike(string name)
{
    var n = name.ToUpperInvariant();
    return n == "ID" || n.EndsWith("_ID") || n.Contains("CODG_") || n.Contains("CODIGO") || n.Contains("NUMR_") || n.Contains("CHAVE");
}
string NormalizeKey(string name)
{
    var n = name.ToUpperInvariant().Replace("__", "_");
    foreach (var prefix in new[] { "CODG_", "CODIGO_", "NUMR_", "ID_" }) if (n.StartsWith(prefix)) n = n[prefix.Length..];
    foreach (var suffix in new[] { "_ID", "ID" }) if (n.EndsWith(suffix) && n.Length > suffix.Length) n = n[..^suffix.Length];
    return n.Replace("_", "");
}
string Q(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
string? Arg(string name) { var i = Array.FindIndex(args, x => x.Equals(name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : null; }
async Task Exec(SqliteConnection connection, string sql) { await using var c = connection.CreateCommand(); c.CommandText = sql; await c.ExecuteNonQueryAsync(); }
async Task Log(string code, string message) { var line = $"{DateTimeOffset.Now:O} [{code}] {message}"; await log.WriteLineAsync(line); }

string BuildRankingMarkdown(List<TableRanking> ranking)
{
    var sb = new StringBuilder("# TOP tabelas mais importantes\n\n| # | Tabela | Registros | Domínio | Ligações | Campos identificados | Score |\n|---:|---|---:|---|---:|---:|---:|\n");
    var i = 0;
    foreach (var item in ranking.Take(300)) sb.AppendLine($"| {++i} | `{item.Table}` | {item.Rows:N0} | {item.Domain} | {item.Relationships:N0} | {item.IdentifiedFields:N0} | {item.Score:N1} |");
    return sb.ToString();
}

string BuildMappingMarkdown(Dictionary<string, DomainTable[]> maps, List<RelationshipResult> relationships)
{
    var sb = new StringBuilder("# Mapeamento inteligente do ERP antigo\n\n");
    foreach (var domain in maps.OrderByDescending(x => x.Value.Sum(t => t.Rows)))
    {
        sb.AppendLine($"## {domain.Key}\n");
        foreach (var table in domain.Value.Take(20))
        {
            sb.AppendLine($"### `{table.Table}`\n");
            sb.AppendLine($"- Registros: **{table.Rows:N0}**");
            sb.AppendLine($"- Confiança do domínio: **{table.DomainConfidence:P0}**");
            if (table.Fields.Length > 0)
            {
                sb.AppendLine("- Campos candidatos:");
                foreach (var f in table.Fields.Take(15)) sb.AppendLine($"  - `{f.Column}` → **{f.Meaning}** ({f.Confidence:P0}), preenchidos {f.NonNull:N0}, distintos {f.Distinct:N0}, exemplos: {string.Join(" | ", f.Examples.Take(3))}");
            }
            var links = relationships.Where(r => r.ChildTable == table.Table || r.ParentTable == table.Table).Take(12).ToArray();
            if (links.Length > 0)
            {
                sb.AppendLine("- Relacionamentos mais fortes:");
                foreach (var r in links) sb.AppendLine($"  - `{r.ChildTable}.{r.ChildColumn}` → `{r.ParentTable}.{r.ParentColumn}` — confiança **{r.Confidence:P0}**, cobertura **{r.Coverage:P0}**");
            }
            sb.AppendLine();
        }
    }
    return sb.ToString();
}

string BuildCsv(List<RelationshipResult> relationships)
{
    var sb = new StringBuilder("child_table;child_column;parent_table;parent_column;sampled;matched;coverage;uniqueness;confidence\n");
    foreach (var r in relationships) sb.AppendLine($"{Csv(r.ChildTable)};{Csv(r.ChildColumn)};{Csv(r.ParentTable)};{Csv(r.ParentColumn)};{r.Sampled};{r.Matched};{r.Coverage:F6};{r.ParentUniqueness:F6};{r.Confidence:F6}");
    return sb.ToString();
}
string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';

sealed class DatabaseStructure { public List<TableInfo> Tables { get; set; } = []; }
sealed class TableInfo { public string Name { get; set; } = ""; public long RowCount { get; set; } public string SuggestedDomain { get; set; } = "NAO_IDENTIFICADO"; public double DomainConfidence { get; set; } public List<ColumnInfo> Columns { get; set; } = []; }
sealed class ColumnInfo { public string Name { get; set; } = ""; public long NonNullCount { get; set; } public long DistinctCount { get; set; } public string SuggestedMeaning { get; set; } = "DESCONHECIDO"; public double MeaningConfidence { get; set; } public List<string> Examples { get; set; } = []; }
sealed record RelationCandidate(string LeftTable, string LeftColumn, long LeftRows, long LeftDistinct, string RightTable, string RightColumn, long RightRows, long RightDistinct);
sealed record RelationshipResult(string ChildTable, string ChildColumn, string ParentTable, string ParentColumn, long Sampled, long Matched, double Coverage, double ParentUniqueness, double Confidence, long ChildRows);
sealed record TableRanking(string Table, long Rows, string Domain, int Relationships, int IdentifiedFields, double Score);
sealed record FieldCandidate(string Column, string Meaning, double Confidence, long NonNull, long Distinct, string[] Examples);
sealed record DomainTable(string Table, long Rows, double DomainConfidence, FieldCandidate[] Fields, RelationshipResult[] Relationships);
sealed record AnalysisResult(DateTimeOffset GeneratedAt, string Database, int TablesWithData, int RelationshipCount, List<RelationshipResult> Relationships, List<TableRanking> Ranking, Dictionary<string, DomainTable[]> Domains);
