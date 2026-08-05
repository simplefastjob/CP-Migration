using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using CPMigration.Shared;

namespace CPMigration.Migration;

public sealed class DomainConsolidationService
{
    private const int MaxRootsPerDomain = 3;
    private const int MaxReferencesPerRelation = 25;
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
        var reviewPath = Path.Combine(consolidatedDirectory, "review.csv");
        await using var reviewWriter = new StreamWriter(reviewPath, false, new UTF8Encoding(true));
        await reviewWriter.WriteLineAsync("domain;table;source_row;code;message");

        var tableLookup = tables.ToDictionary(t => t.OriginalName, StringComparer.OrdinalIgnoreCase);

        foreach (var group in tables.Where(t => t.RowCount > 0).GroupBy(t => t.Domain, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            EnsureFreeSpace(consolidatedDirectory, 512L * 1024 * 1024);

            var domain = group.Key;
            var domainTables = group.ToArray();
            var roots = SelectRoots(domainTables, relationships).Take(MaxRootsPerDomain).ToArray();
            var outputPath = Path.Combine(consolidatedDirectory, Sanitize(domain) + ".ndjson.gz");
            var partialPath = outputPath + ".partial";

            TryDelete(partialPath);
            _logger.Write(Severity.Info, "CONSOLIDATE_DOMAIN", $"{domain}: {domainTables.Length} tabelas; {roots.Length} raízes; saída compactada e sem duplicar registros relacionados", outputPath);

            try
            {
                await WriteDomainFileAsync(
                    partialPath,
                    domain,
                    roots,
                    tableLookup,
                    relationships,
                    connection,
                    reviewWriter,
                    summary,
                    ct);

                ct.ThrowIfCancellationRequested();
                File.Move(partialPath, outputPath, true);
            }
            catch
            {
                TryDelete(partialPath);
                throw;
            }
        }

        await reviewWriter.FlushAsync(ct);
        await File.WriteAllTextAsync(
            Path.Combine(consolidatedDirectory, "consolidation-summary.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }),
            ct);
        await File.WriteAllTextAsync(
            Path.Combine(consolidatedDirectory, "LEIA-ME.txt"),
            "Os arquivos consolidados usam .ndjson.gz. Eles contêm os dados principais e referências compactas. Os registros completos permanecem nas pastas de exportação bruta e no erp_intermediario.sqlite, evitando duplicação extrema e falta de espaço em disco.",
            ct);

        return summary;
    }

    private async Task WriteDomainFileAsync(
        string partialPath,
        string domain,
        IReadOnlyList<TableProfile> roots,
        IReadOnlyDictionary<string, TableProfile> tableLookup,
        IReadOnlyList<RelationshipCandidate> relationships,
        SqliteConnection connection,
        StreamWriter reviewWriter,
        ConsolidationSummary summary,
        CancellationToken ct)
    {
        await using (var file = new FileStream(
            partialPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest, leaveOpen: false))
        await using (var writer = new StreamWriter(gzip, new UTF8Encoding(false), 1024 * 1024, leaveOpen: false))
        {
            foreach (var root in roots)
            {
                await foreach (var source in ReadRowsAsync(connection, root, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    var key = ResolveBusinessKey(root, source.Data, source.RowNumber);
                    var references = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

                    foreach (var relation in FindRelations(root, relationships))
                    {
                        var rootIsParent = relation.ParentTable.Equals(root.OriginalName, StringComparison.OrdinalIgnoreCase);
                        var relatedName = rootIsParent ? relation.ChildTable : relation.ParentTable;
                        if (!tableLookup.TryGetValue(relatedName, out var relatedTable)) continue;

                        var rootColumn = rootIsParent ? relation.ParentColumn : relation.ChildColumn;
                        var relatedColumn = rootIsParent ? relation.ChildColumn : relation.ParentColumn;
                        if (!source.Data.TryGetValue(rootColumn, out var value) || string.IsNullOrWhiteSpace(value?.ToString())) continue;

                        var refs = await ReadRelatedReferencesAsync(
                            connection,
                            relatedTable,
                            relatedColumn,
                            value!.ToString()!,
                            MaxReferencesPerRelation + 1,
                            ct);
                        if (refs.Count == 0) continue;

                        var truncated = refs.Count > MaxReferencesPerRelation;
                        references[relatedTable.OriginalName] = new
                        {
                            joinColumn = relatedColumn,
                            joinValue = value,
                            confidence = relation.Confidence,
                            rows = refs.Take(MaxReferencesPerRelation).ToArray(),
                            truncated
                        };

                        if (truncated)
                        {
                            summary.ReviewRecords++;
                            await reviewWriter.WriteLineAsync(
                                $"{Csv(domain)};{Csv(root.OriginalName)};{source.RowNumber};RELATED_TRUNCATED;{Csv($"{relatedTable.OriginalName} possui mais de {MaxReferencesPerRelation} referências; os dados completos permanecem na exportação bruta")}");
                        }
                    }

                    var document = new
                    {
                        type = DomainToDocumentType(domain),
                        sourceId = key,
                        source = new { table = root.OriginalName, sqliteTable = root.SqliteName, row = source.RowNumber },
                        domain,
                        core = NormalizeCore(domain, source.Data),
                        references,
                        rawLocation = new { sqliteDatabase = "erp_intermediario.sqlite", table = root.SqliteName, row = source.RowNumber },
                        validation = new { valid = true, relatedTables = references.Count }
                    };

                    await writer.WriteLineAsync(JsonSerializer.Serialize(document, JsonOptions));
                    summary.Documents++;
                    summary.ByDomain[domain] = summary.ByDomain.GetValueOrDefault(domain) + 1;
                }
            }

            await writer.FlushAsync(ct);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Não mascara o erro original do pipeline.
        }
    }

    private static void EnsureFreeSpace(string path, long requiredBytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? throw new IOException("Não foi possível identificar a unidade de saída.");
        var drive = new DriveInfo(root);
        if (drive.AvailableFreeSpace < requiredBytes)
            throw new IOException($"Espaço livre insuficiente em {root}. Livre: {FormatBytes(drive.AvailableFreeSpace)}; mínimo necessário para continuar: {FormatBytes(requiredBytes)}. Escolha outra unidade de saída.");
    }

    private static string FormatBytes(long value) => $"{value / 1024d / 1024d / 1024d:N2} GB";

    private static List<TableProfile> SelectRoots(IReadOnlyList<TableProfile> tables, IReadOnlyList<RelationshipCandidate> relationships)
    {
        var incoming = relationships.GroupBy(r => r.ChildTable, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var outgoing = relationships.GroupBy(r => r.ParentTable, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        return tables.Select(t => new
        {
            Table = t,
            Score = t.Columns.Count(c => c.IsPrimaryKeyCandidate) * 8 + outgoing.GetValueOrDefault(t.OriginalName) * 3 - incoming.GetValueOrDefault(t.OriginalName) + RootNameBonus(t.OriginalName)
        }).OrderByDescending(x => x.Score).ThenByDescending(x => x.Table.RowCount).Select(x => x.Table).ToList();
    }

    private static int RootNameBonus(string name)
    {
        var value = name.ToUpperInvariant();
        string[] terms = ["ENTIDADES", "PRODUTOS", "PEDIDO", "VENDA", "COMPRA", "TITULO", "NOTA_FISCAL", "INVENTARIO", "GARANTIA"];
        return terms.Any(value.Contains) ? 20 : 0;
    }

    private static IEnumerable<RelationshipCandidate> FindRelations(TableProfile root, IReadOnlyList<RelationshipCandidate> relationships) =>
        relationships.Where(r => r.Confidence >= .80 && (r.ParentTable.Equals(root.OriginalName, StringComparison.OrdinalIgnoreCase) || r.ChildTable.Equals(root.OriginalName, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.Confidence).Take(30);

    private static string ResolveBusinessKey(TableProfile table, Dictionary<string, object?> row, long rowNumber)
    {
        var candidates = table.Columns.Where(c => c.IsPrimaryKeyCandidate).Select(c => c.Name).Concat(["ID", "PARENTD_ID", "COMPROD_ID", "PCIPROD_ID", "CODG_ENTIDADE", "CODG_PRODUTO", "NUMR_PEDIDO", "NUMR_NOTA", "CODIGO"]);
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            if (row.TryGetValue(candidate, out var value) && !string.IsNullOrWhiteSpace(value?.ToString())) return value!.ToString()!;
        return rowNumber.ToString();
    }

    private static Dictionary<string, object?> NormalizeCore(string domain, Dictionary<string, object?> raw)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var mappings = DomainMappings.GetValueOrDefault(domain) ?? GenericMappings;
        foreach (var mapping in mappings)
        {
            var found = mapping.Value.FirstOrDefault(name => raw.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value?.ToString()));
            if (found is not null) result[mapping.Key] = raw[found];
        }
        return result;
    }

    private static readonly Dictionary<string, string[]> GenericMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = ["ID", "CODIGO", "CODG_CODIGO"],
        ["description"] = ["DESCRICAO", "DESC_DESCRICAO", "NOME", "NOME_RAZAO_SOCIAL"],
        ["status"] = ["STATUS", "SITUACAO", "INDR_ATIVO", "INDR_STATUS"]
    };

    private static readonly Dictionary<string, Dictionary<string, string[]>> DomainMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CLIENTES-ENTIDADES"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = ["ID", "PARENTD_ID", "CODG_ENTIDADE"],
            ["name"] = ["NOME_RAZAO_SOCIAL", "NOME", "RAZAO_SOCIAL"],
            ["tradeName"] = ["NOME_FANTASIA", "FANTASIA"],
            ["cpfCnpj"] = ["NUMR_CGC", "CPF_CNPJ", "CNPJ", "CPF"],
            ["email"] = ["NOME_EMAIL", "EMAIL"],
            ["phone"] = ["FONE_CONTATO", "TELEFONE", "CELULAR"]
        },
        ["PRODUTOS-CATALOGO"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = ["ID", "COMPROD_ID", "PCIPROD_ID"],
            ["code"] = ["CODG_PRODUTO", "CODIGO", "REFERENCIA"],
            ["description"] = ["DESC_PRODUTO", "DESCRICAO", "NOME_PRODUTO"],
            ["brand"] = ["MARCA", "DESC_MARCA"]
        },
        ["ESTOQUE-LOGISTICA"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["productId"] = ["COMPROD_ID", "PCIPROD_ID", "PRODUTO_ID"],
            ["quantity"] = ["QTDE_ESTOQUE", "QUANTIDADE", "SALDO"],
            ["location"] = ["CODG_LOCACAO", "LOCACAO", "ENDERECO_ESTOQUE"],
            ["warehouse"] = ["PCIDEPO_ID", "CODG_DEPOSITO", "DEPOSITO"]
        },
        ["FINANCEIRO"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = ["ID", "FINTITU_ID"],
            ["entityId"] = ["PARENTD_ID", "CLIENTE_ID", "FORNECEDOR_ID"],
            ["amount"] = ["VALR_TITULO", "VALOR", "VALR_ORIGINAL"],
            ["dueDate"] = ["DATA_VENCIMENTO", "DTHR_VENCIMENTO"],
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

    private static async IAsyncEnumerable<SourceRecord> ReadRowsAsync(
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
            var rowNumber = data.TryGetValue("__SOURCE_ROW", out var value) && long.TryParse(value?.ToString(), out var parsed) ? parsed : 0;
            yield return new SourceRecord(rowNumber, data);
        }
    }

    private static async Task<List<long>> ReadRelatedReferencesAsync(
        SqliteConnection connection,
        TableProfile table,
        string column,
        string value,
        int limit,
        CancellationToken ct)
    {
        var result = new List<long>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT __SOURCE_ROW FROM {Q(table.SqliteName)} WHERE {Q(column)} = @value LIMIT @limit";
        command.Parameters.AddWithValue("@value", value);
        command.Parameters.AddWithValue("@limit", limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            if (!reader.IsDBNull(0)) result.Add(Convert.ToInt64(reader.GetValue(0)));
        return result;
    }

    private static Dictionary<string, object?> ReadDictionary(SqliteDataReader reader)
    {
        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
            data[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString();
        return data;
    }

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
    private static string Sanitize(string value) => new(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
    private static string Q(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    private sealed record SourceRecord(long RowNumber, Dictionary<string, object?> Data);
}

public sealed class ConsolidationSummary
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.Now;
    public long Documents { get; set; }
    public int ReviewRecords { get; set; }
    public Dictionary<string, long> ByDomain { get; } = new(StringComparer.OrdinalIgnoreCase);
}
