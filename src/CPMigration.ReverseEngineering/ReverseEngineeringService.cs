using System.Text.Json;
using Microsoft.Data.Sqlite;
using CPMigration.Shared;

namespace CPMigration.ReverseEngineering;

public sealed class ReverseEngineeringService
{
    private readonly IPipelineLogger _logger;
    public ReverseEngineeringService(IPipelineLogger logger) => _logger = logger;

    public async Task<IReadOnlyList<RelationshipCandidate>> AnalyzeAsync(
        string databasePath,
        IList<TableProfile> tables,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        foreach (var table in tables)
        {
            table.Domain = DomainClassifier.Classify(table.OriginalName, table.Columns.Select(c => c.Name));
            table.State = table.RowCount == 0 ? TableState.Empty : table.State;
            foreach (var column in table.Columns)
            {
                column.IsPrimaryKeyCandidate = IsPrimaryKeyName(column.Name) && column.NonEmptyCount == table.RowCount && table.RowCount > 0;
                column.IsForeignKeyCandidate = IsForeignKeyName(column.Name);
            }
        }

        var relationships = await DiscoverRelationshipsAsync(databasePath, tables, cancellationToken);
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "ERP_MAP.json"), JsonSerializer.Serialize(new
        {
            generatedAt = DateTimeOffset.Now,
            tables,
            relationships
        }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        await WriteRelationshipCsvAsync(Path.Combine(outputDirectory, "relationships.csv"), relationships, cancellationToken);
        return relationships;
    }

    private async Task<List<RelationshipCandidate>> DiscoverRelationshipsAsync(string databasePath, IList<TableProfile> tables, CancellationToken ct)
    {
        var result = new List<RelationshipCandidate>();
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync(ct);
        var candidates = tables
            .SelectMany(t => t.Columns.Where(c => c.IsPrimaryKeyCandidate || c.IsForeignKeyCandidate).Select(c => (Table: t, Column: c)))
            .ToArray();

        long processed = 0;
        var total = (long)candidates.Length * Math.Max(0, candidates.Length - 1) / 2;
        for (var i = 0; i < candidates.Length; i++)
        for (var j = i + 1; j < candidates.Length; j++)
        {
            ct.ThrowIfCancellationRequested();
            processed++;
            var a = candidates[i]; var b = candidates[j];
            if (a.Table.SqliteName == b.Table.SqliteName) continue;
            var nameScore = NameSimilarity(a.Column.Name, b.Column.Name);
            if (nameScore < 0.45) continue;
            var overlap = await SampleOverlapAsync(connection, a.Table.SqliteName, a.Column.Name, b.Table.SqliteName, b.Column.Name, ct);
            var confidence = Math.Round(nameScore * 0.45 + overlap * 0.55, 4);
            if (confidence < 0.70) continue;
            result.Add(new RelationshipCandidate(a.Table.OriginalName, a.Column.Name, b.Table.OriginalName, b.Column.Name, nameScore, overlap, confidence, "similaridade de nome + sobreposição amostral"));
            if (processed % 10000 == 0) _logger.Write(Severity.Info, "REL_PROGRESS", $"{processed}/{total} pares; {result.Count} relações");
        }
        return result.OrderByDescending(r => r.Confidence).ToList();
    }

    private static async Task<double> SampleOverlapAsync(SqliteConnection c, string ta, string ca, string tb, string cb, CancellationToken ct)
    {
        var sql = $"WITH A AS (SELECT DISTINCT {Q(ca)} V FROM {Q(ta)} WHERE {Q(ca)} IS NOT NULL LIMIT 1000), B AS (SELECT DISTINCT {Q(cb)} V FROM {Q(tb)} WHERE {Q(cb)} IS NOT NULL LIMIT 1000) SELECT CAST(COUNT(*) AS REAL)/(SELECT MAX(1,COUNT(*)) FROM A) FROM A INNER JOIN B USING(V);";
        await using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? 0 : Convert.ToDouble(value);
    }

    private static bool IsPrimaryKeyName(string n) => n.Equals("ID", StringComparison.OrdinalIgnoreCase) || n.EndsWith("_ID", StringComparison.OrdinalIgnoreCase);
    private static bool IsForeignKeyName(string n) => n.EndsWith("_ID", StringComparison.OrdinalIgnoreCase) || n.Contains("CODG_", StringComparison.OrdinalIgnoreCase);
    private static double NameSimilarity(string a, string b)
    {
        a = Normalize(a); b = Normalize(b);
        if (a == b) return 1;
        if (a.EndsWith(b) || b.EndsWith(a)) return .85;
        var sa = a.Split('_').ToHashSet(); var sb = b.Split('_').ToHashSet();
        return sa.Union(sb).Count() == 0 ? 0 : (double)sa.Intersect(sb).Count() / sa.Union(sb).Count();
    }
    private static string Normalize(string v) => v.ToUpperInvariant().Replace("CODG_", "").Replace("NUMR_", "");
    private static string Q(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static async Task WriteRelationshipCsvAsync(string path, IEnumerable<RelationshipCandidate> rows, CancellationToken ct)
    {
        await using var w = new StreamWriter(path, false, System.Text.Encoding.UTF8);
        await w.WriteLineAsync("parent_table;parent_column;child_table;child_column;name_score;value_overlap;confidence;evidence");
        foreach (var r in rows) await w.WriteLineAsync($"{r.ParentTable};{r.ParentColumn};{r.ChildTable};{r.ChildColumn};{r.NameScore};{r.ValueOverlap};{r.Confidence};{r.Evidence}");
    }
}

public static class DomainClassifier
{
    private static readonly (string Domain, string[] Terms)[] Rules =
    [
        ("CLIENTES-ENTIDADES", ["ENTIDADE","CLIENTE","PESSOA","FORNECEDOR"]),
        ("PRODUTOS-CATALOGO", ["PRODUTO","PECA","ITEM","APLICACAO","MARCA"]),
        ("PRECOS-CUSTOS", ["PRECO","CUSTO","RENTABILIDADE","MARGEM"]),
        ("ESTOQUE-LOGISTICA", ["ESTOQUE","LOCACAO","DEPOSITO","LOTE","INVENTARIO","KARDEX"]),
        ("COMPRAS-SUPRIMENTOS", ["COMPRA","COTACAO","SUPRIMENTO","REQUISICAO"]),
        ("VENDAS-COMERCIAL", ["VENDA","PEDIDO","NEGOCIACAO","COMISSAO"]),
        ("FINANCEIRO", ["FIN_","TITULO","PAGAMENTO","BAIXA","BANCO","CAIXA","CREDITO"]),
        ("FISCAL-TRIBUTARIO", ["NFE","NOTA_FISCAL","FISCAL","NCM","CFOP","TRIBUTO","ICMS"]),
        ("CONTABILIDADE", ["CTB_","CONTABIL","BALANCETE","CENTRO_CUSTO"]),
        ("CRM-ATENDIMENTO", ["CRM_","ATENDIMENTO","OCORRENCIA","RECLAMACAO"]),
        ("GARANTIAS-DEVOLUCOES", ["GARANTIA","DEVOLUCAO","ESTORNO"]),
        ("SERVICOS-OS", ["ORDEM_SERVICO","SERVICO","MAODEOBRA"]),
        ("USUARIOS-SEGURANCA", ["USUARIO","PERMISSAO","ACESSO","LOGIN"]),
        ("CONFIGURACOES-PARAMETROS", ["PARAMETRO","CONFIG","VERSAO"]),
        ("INTEGRACOES-LOGS", ["INTEGRACAO","LOG_","IMPORT","EXPORT"])
    ];

    public static string Classify(string table, IEnumerable<string> columns)
    {
        var text = (table + " " + string.Join(' ', columns)).ToUpperInvariant();
        return Rules.Select(r => new { r.Domain, Score = r.Terms.Count(text.Contains) }).OrderByDescending(x => x.Score).FirstOrDefault(x => x.Score > 0)?.Domain ?? "NAO_IDENTIFICADO";
    }
}
