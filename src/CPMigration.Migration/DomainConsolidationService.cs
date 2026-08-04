using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using CPMigration.Shared;

namespace CPMigration.Migration;

public sealed class DomainConsolidationService
{
    private readonly IPipelineLogger _logger;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public DomainConsolidationService(IPipelineLogger logger) => _logger = logger;

    public async Task<ConsolidationSummary> ConsolidateAsync(
        string databasePath,
        IReadOnlyList<TableProfile> tables,
        IReadOnlyList<RelationshipCandidate> relationships,
        string migrationDirectory,
        CancellationToken ct = default)
    {
        var consolidatedDirectory = Path.Combine(migrationDirectory, "CONSOLIDADO");
        Directory.CreateDirectory(consolidatedDirectory);

        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync(ct);

        var summary = new ConsolidationSummary();
        var review = new List<ReviewRecord>();
        var tableByOriginal = tables.ToDictionary(t => t.OriginalName, StringComparer.OrdinalIgnoreCase);

        foreach (var domainGroup in tables.Where(t => t.RowCount > 0).GroupBy(t => t.Domain, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var domain = domainGroup.Key;
            var domainTables = domainGroup.ToArray();
            var roots = SelectRoots(domainTables, relationships);
            var outputPath = Path.Combine(consolidatedDirectory, Sanitize(domain) + ".ndjson");
            _logger.Write(Severity.Info, "CONSOLIDATE_DOMAIN", $"{domain}: {domainTables.Length} tabelas; {roots.Count} raízes", outputPath);

            await using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false));
            foreach (var root in roots)
            {
                await foreach (var row in ReadRowsAsync(connection, root, ct))
                {
                    var key = ResolveBusinessKey(root, row);
                    if (key is null)
                    {
                        review.Add(new ReviewRecord(domain, root.OriginalName, row.SourceRow, "ROOT_WITHOUT_KEY", "Registro raiz sem chave de negócio detectável"));
                        continue;
                    }

                    var related = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var relation in FindRelations(root, relationships))
                    {
                        if (!tableByOriginal.TryGetValue(relation.ChildTable, out var child)) continue;
                        var rootColumn = relation.ParentTable.Equals(root.OriginalName, StringComparison.OrdinalIgnoreCase)
                            ? relation.ParentColumn : relation.ChildColumn;
                        var childColumn = relation.ParentTable.Equals(root.OriginalName, StringComparison.OrdinalIgnoreCase)
                            ? relation.ChildColumn : relation.ParentColumn;
                        var childTable = relation.ParentTable.Equals(root.OriginalName, StringComparison.OrdinalIgnoreCase)
                            ? tableByOriginal.GetValueOrDefault(relation.ChildTable) : tableByOriginal.GetValueOrDefault(relation.ParentTable);
                        if (childTable is null || !row.Data.TryGetValue(rootColumn, out var value) || value is null) continue;

                        var records = await ReadRelatedAsync(connection, childTable, childColumn, value.ToString()!, 250, ct);
                        if (records.Count > 0) related[childTable.OriginalName] = records;
                        if (records.Count == 250)
                            review.Add(new ReviewRecord(domain, root.OriginalName, row.SourceRow, "RELATED_LIMIT", $"{childTable.OriginalName} atingiu o limite de 250 registros relacionados"));
                    }

                    var document = new
                    {
                        type = DomainToDocumentType(domain),
                        sourceId = key,
                        source = new { table = root.OriginalName, sqliteTable = root.SqliteName, row = row.SourceRow },
                        domain,
                        core = NormalizeCore(domain, row.Data),
                        raw = row.Data,
                        related,
                        validation = new
                        {
                            valid = true,
                            warnings = related.Count == 0 ? new[] { "Nenhuma tabela relacionada encontrada com confiança suficiente" } : Array.Empty<string>()
                        }
                    };
                    await writer.WriteLineAsync(JsonSerializer.Serialize(document, JsonOptions));
                    summary.Documents++;
                    summary.ByDomain[domain] = summary.ByDomain.GetValueOrDefault(domain) + 1;
                }
            }
        }

        await WriteReviewAsync(Path.Combine(consolidatedDirectory, "review.csv"), review, ct);
        summary.ReviewRecords = review.Count;
        await File.WriteAllTextAsync(
            Path.Combine(consolidatedDirectory, "consolidation-summary.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }), ct);
        return summary;
    }

    private static List<TableProfile> SelectRoots(IReadOnlyList<TableProfile> tables, IReadOnlyList<RelationshipCandidate> relationships)
    {
        var incoming = relationships.GroupBy(r => r.ChildTable, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var outgoing = relationships.GroupBy(r => r.ParentTable, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var scored = tables.Select(t => new
        {
            Table = t,
            Score = (t.RowCount > 0 ? 10 : 0)
                + t.Columns.Count(c => c.IsPrimaryKeyCandidate) * 8
                + outgoing.GetValueOrDefault(t.OriginalName) * 3
                - incoming.GetValueOrDefault(t.OriginalName)
                + RootNameBonus(t.OriginalName)
        }).OrderByDescending(x => x.Score).ThenByDescending(x => x.Table.RowCount).ToArray();

        var roots = scored.Where(x => x.Score > 10).Take(12).Select(x => x.Table).ToList();
        if (roots.Count == 0 && scored.Length > 0) roots.Add(scored[0].Table);
        return roots;
    }

    private static int RootNameBonus(string name)
    {
        var value = name.ToUpperInvariant();
        var terms = new[] { "ENTIDADES", "PRODUTOS", "PEDIDO", "VENDA", "COMPRA", "TITULO", "NOTA_FISCAL", "INVENTARIO", "GARANTIA", "ORDEM_SERVICO" };
        return terms.Any(value.Contains) ? 20 : 0;
    }

    private static IEnumerable<RelationshipCandidate> FindRelations(TableProfile root, IReadOnlyList<RelationshipCandidate> relationships) =>
        relationships.Where(r => r.Confidence >= .75 &&
            (r.ParentTable.Equals(root.OriginalName, StringComparison.OrdinalIgnoreCase) ||
             r.ChildTable.Equals(root.OriginalName, StringComparison.OrdinalIgnoreCase)))
        .OrderByDescending(r => r.Confidence)
        .Take(80);

    private static string? ResolveBusinessKey(TableProfile table, SourceRow row)
    {
        var candidates = table.Columns
            .Where(c => c.IsPrimaryKeyCandidate)
            .Select(c => c.Name)
            .Concat(new[] { "ID", "CODG_ENTIDADE", "CODG_PRODUTO", "NUMR_PEDIDO", "NUMR_NOTA", "CODIGO" });
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            if (row.Data.TryGetValue(candidate, out var value) && value is not null && !string.IsNullOrWhiteSpace(value.ToString()))
                return value.ToString();
        return row.SourceRow.ToString();
    }

    private static Dictionary<string, object?> NormalizeCore(string domain, Dictionary<string, object?> raw)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var mappings = DomainFieldMappings.GetValueOrDefault(domain) ?? GenericMappings;
        foreach (var mapping in mappings)
        {
            var found = mapping.Value.FirstOrDefault(name => raw.TryGetValue(name, out var value) && value is not null && !string.IsNullOrWhiteSpace(value.ToString()));
            if (found is not null) result[mapping.Key] = raw[found];
        }
        return result;
    }

    private static readonly Dictionary<string, string[]> GenericMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = ["ID", "CODIGO", "CODG_CODIGO"],
        ["description"] = ["DESCRICAO", "DESC_DESCRICAO", "NOME", "NOME_RAZAO_SOCIAL"],
        ["status"] = ["STATUS", "SITUACAO", "INDR_ATIVO", "INDR_STATUS"],
        ["createdAt"] = ["DATA_CADASTRO", "DTHR_CADASTRO", "DATA_INCLUSAO"]
    };

    private static readonly Dictionary<string, Dictionary<string, string[]>> DomainFieldMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CLIENTES-ENTIDADES"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = ["ID", "PARENTD_ID", "CODG_ENTIDADE"], ["name"] = ["NOME_RAZAO_SOCIAL", "NOME", "RAZAO_SOCIAL"],
            ["tradeName"] = ["NOME_FANTASIA", "FANTASIA"], ["cpfCnpj"] = ["NUMR_CGC", "CPF_CNPJ", "CNPJ", "CPF"],
            ["email"] = ["NOME_EMAIL", "EMAIL"], ["phone"] = ["FONE_CONTATO", "TELEFONE", "CELULAR"]
        },
        ["PRODUTOS-CATALOGO"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = ["ID", "COMPROD_ID", "PCIPROD_ID"], ["code"] = ["CODG_PRODUTO", "CODIGO", "REFERENCIA"],
            ["description"] = ["DESC_PRODUTO", "DESCRICAO", "NOME_PRODUTO"], ["brand"] = ["MARCA", "DESC_MARCA"]
        },
        ["ESTOQUE-LOGISTICA"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["productId"] = ["COMPROD_ID", "PCIPROD_ID", "PRODUTO_ID"], ["quantity"] = ["QTDE_ESTOQUE", "QUANTIDADE", "SALDO"],
            ["location"] = ["CODG_LOCACAO", "LOCACAO", "ENDERECO_ESTOQUE"], ["warehouse"] = ["PCIDEPO_ID", "CODG_DEPOSITO", "DEPOSITO"]
        },
        ["FINANCEIRO"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = ["ID", "FINTITU_ID"], ["entityId"] = ["PARENTD_ID", "CLIENTE_ID", "FORNECEDOR_ID"],
            ["amount"] = ["VALR_TITULO", "VALOR", "VALR_ORIGINAL"], ["dueDate"] = ["DATA_VENCIMENTO", "DTHR_VENCIMENTO"],
            ["status"] = ["SITUACAO", "INDR_STATUS", "STATUS"]
        }
    };

    private static string DomainToDocumentType(string domain) => domain switch
    {
        "CLIENTES-ENTIDADES" => "entity",
        "PRODUTOS-CATALOGO" => "product",
        "ESTOQUE-LOGISTICA" => "stock",
        "COMPRAS-SUPRIMENTOS" => "purchase",
        "VENDAS-COMERCIAL" => "sale",
        "FINANCEIRO" => "financial",
        "FISCAL-TRIBUTARIO" => "fiscal",
        "CONTABILIDADE" => "accounting",
        "GARANTIAS-DEVOLUCOES" => "warranty",
        _ => "generic"
    };

    private static async IAsyncEnumerable<SourceRow> ReadRowsAsync(
        SqliteConnection connection,
        TableProfile table,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {Q(table.SqliteName)}";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var data = ReadDictionary(reader);
            var sourceRow = data.TryGetValue("__SOURCE_ROW", out var value) && long.TryParse(value?.ToString(), out var row) ? row : 0;
            yield return new SourceRow(sourceRow, data);
        }
    }

    private static async Task<List<Dictionary<string, object?>>> ReadRelatedAsync(
        SqliteConnection connection, TableProfile table, string column, string value, int limit, CancellationToken ct)
    {
        var result = new List<Dictionary<string, object?>>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {Q(table.SqliteName)} WHERE {Q(column)} = @value LIMIT @limit";
        command.Parameters.AddWithValue("@value", value);
        command.Parameters.AddWithValue("@limit", limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadDictionary(reader));
        return result;
    }

    private static Dictionary<string, object?> ReadDictionary(SqliteDataReader reader)
    {
        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++) data[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString();
        return data;
    }

    private static async Task WriteReviewAsync(string path, IEnumerable<ReviewRecord> records, CancellationToken ct)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        await writer.WriteLineAsync("domain;table;source_row;code;message");
        foreach (var record in records)
            await writer.WriteLineAsync($"{Escape(record.Domain)};{Escape(record.Table)};{record.SourceRow};{Escape(record.Code)};{Escape(record.Message)}");
    }

    private static string Escape(string value) => '"' + value.Replace("\"", "\"\"") + '"';
    private static string Sanitize(string value) => new(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
    private static string Q(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private sealed record SourceRow(long SourceRow, Dictionary<string, object?> Data);
    private sealed record ReviewRecord(string Domain, string Table, long SourceRow, string Code, string Message);
}

public sealed class ConsolidationSummary
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.Now;
    public long Documents { get; set; }
    public int ReviewRecords { get; set; }
    public Dictionary<string, long> ByDomain { get; } = new(StringComparer.OrdinalIgnoreCase);
}
