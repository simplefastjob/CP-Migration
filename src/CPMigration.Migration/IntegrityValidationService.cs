using System.Text.Json;
using Microsoft.Data.Sqlite;
using CPMigration.Shared;

namespace CPMigration.Migration;

public sealed record ValidationFinding(
    string Severity,
    string Code,
    string Message,
    string? Table = null,
    string? Column = null,
    long? Count = null);

public sealed class IntegrityValidationService
{
    private readonly IPipelineLogger _logger;
    public IntegrityValidationService(IPipelineLogger logger) => _logger = logger;

    public async Task<IReadOnlyList<ValidationFinding>> ValidateAsync(
        string databasePath,
        IReadOnlyList<TableProfile> tables,
        IReadOnlyList<RelationshipCandidate> relationships,
        string outputDirectory,
        CancellationToken ct = default)
    {
        var findings = new List<ValidationFinding>();
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync(ct);

        foreach (var table in tables)
        {
            ct.ThrowIfCancellationRequested();
            if (table.RowCount == 0)
            {
                findings.Add(new("INFO", "EMPTY_TABLE", "Tabela sem registros.", table.OriginalName, Count: 0));
                continue;
            }

            if (table.MalformedRows > 0)
                findings.Add(new("WARNING", "MALFORMED_ROWS", "Tabela possui linhas que não puderam ser interpretadas.", table.OriginalName, Count: table.MalformedRows));

            var candidates = table.Columns.Where(c => c.IsPrimaryKeyCandidate).ToArray();
            if (candidates.Length == 0)
                findings.Add(new("WARNING", "NO_PRIMARY_KEY", "Nenhuma chave principal segura foi identificada.", table.OriginalName));

            foreach (var column in candidates)
            {
                var duplicateCount = await ScalarLongAsync(connection,
                    $"SELECT COUNT(*) FROM (SELECT {Q(column.Name)} FROM {Q(table.SqliteName)} WHERE {Q(column.Name)} IS NOT NULL GROUP BY {Q(column.Name)} HAVING COUNT(*) > 1)", ct);
                if (duplicateCount > 0)
                    findings.Add(new("WARNING", "DUPLICATE_KEY", "Possível chave possui valores duplicados.", table.OriginalName, column.Name, duplicateCount));
            }
        }

        foreach (var relation in relationships.Where(r => r.Confidence >= 0.80))
        {
            ct.ThrowIfCancellationRequested();
            var parent = tables.FirstOrDefault(t => t.OriginalName.Equals(relation.ParentTable, StringComparison.OrdinalIgnoreCase));
            var child = tables.FirstOrDefault(t => t.OriginalName.Equals(relation.ChildTable, StringComparison.OrdinalIgnoreCase));
            if (parent is null || child is null) continue;

            var orphanCount = await ScalarLongAsync(connection,
                $"SELECT COUNT(*) FROM {Q(child.SqliteName)} c LEFT JOIN {Q(parent.SqliteName)} p ON c.{Q(relation.ChildColumn)} = p.{Q(relation.ParentColumn)} WHERE c.{Q(relation.ChildColumn)} IS NOT NULL AND p.{Q(relation.ParentColumn)} IS NULL", ct);
            if (orphanCount > 0)
                findings.Add(new("WARNING", "ORPHAN_REFERENCE", "Registros apontam para uma chave não encontrada na tabela relacionada.", child.OriginalName, relation.ChildColumn, orphanCount));
        }

        Directory.CreateDirectory(outputDirectory);
        var jsonPath = Path.Combine(outputDirectory, "validation-findings.json");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(findings, new JsonSerializerOptions { WriteIndented = true }), ct);

        var csvPath = Path.Combine(outputDirectory, "validation-findings.csv");
        await using (var writer = new StreamWriter(csvPath, false, System.Text.Encoding.UTF8))
        {
            await writer.WriteLineAsync("severity;code;table;column;count;message");
            foreach (var item in findings)
                await writer.WriteLineAsync($"{Escape(item.Severity)};{Escape(item.Code)};{Escape(item.Table)};{Escape(item.Column)};{item.Count};{Escape(item.Message)}");
        }

        _logger.Write(Severity.Info, "VALIDATION_DONE", $"Validação concluída: {findings.Count} apontamentos.", jsonPath);
        return findings;
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    private static string Q(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    private static string Escape(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";
}
