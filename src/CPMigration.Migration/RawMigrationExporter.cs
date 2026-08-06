using System.Text.Json;
using Microsoft.Data.Sqlite;
using CPMigration.Shared;

namespace CPMigration.Migration;

public sealed class RawMigrationExporter
{
    private readonly IPipelineLogger _logger;
    public RawMigrationExporter(IPipelineLogger logger) => _logger = logger;

    public async Task ExportAsync(string databasePath, IEnumerable<TableProfile> tables, string migrationDirectory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(migrationDirectory);
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync(ct);

        var manifest = new List<object>();
        foreach (var table in tables)
        {
            ct.ThrowIfCancellationRequested();
            var domain = Sanitize(table.Domain);
            var folder = Path.Combine(migrationDirectory, domain);
            Directory.CreateDirectory(folder);
            var outputPath = Path.Combine(folder, Sanitize(table.OriginalName) + ".ndjson");
            _logger.Write(Severity.Info, "EXPORT_TABLE", table.OriginalName, outputPath);

            await using var writer = new StreamWriter(outputPath, false, new System.Text.UTF8Encoding(false));
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {Q(table.SqliteName)}";
            await using var reader = await command.ExecuteReaderAsync(ct);
            long exported = 0;
            while (await reader.ReadAsync(ct))
            {
                var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                    data[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString();
                var envelope = new
                {
                    source = new { table = table.OriginalName, sqliteTable = table.SqliteName, path = table.SourcePath, row = data.GetValueOrDefault("__SOURCE_ROW") },
                    domain = table.Domain,
                    state = table.State.ToString(),
                    data
                };
                await writer.WriteLineAsync(JsonSerializer.Serialize(envelope));
                exported++;
            }
            manifest.Add(new { table = table.OriginalName, table.SqliteName, table.Domain, table.State, table.RowCount, exported, file = Path.GetRelativePath(migrationDirectory, outputPath), columns = table.Columns.Select(c => c.Name) });
        }

        await File.WriteAllTextAsync(Path.Combine(migrationDirectory, "manifesto-completo.json"), JsonSerializer.Serialize(new { generatedAt = DateTimeOffset.Now, tables = manifest }, new JsonSerializerOptions { WriteIndented = true }), ct);
        await WriteCatalogAsync(Path.Combine(migrationDirectory, "catalogo-tabelas.csv"), tables, ct);
        await File.WriteAllTextAsync(Path.Combine(migrationDirectory, "LEIA-ME.txt"), "Todos os registros foram preservados por tabela e domínio. Arquivos em NAO_IDENTIFICADO exigem revisão humana antes de qualquer gravação no ERP novo.", ct);
    }

    private static async Task WriteCatalogAsync(string path, IEnumerable<TableProfile> tables, CancellationToken ct)
    {
        await using var writer = new StreamWriter(path, false, System.Text.Encoding.UTF8);
        await writer.WriteLineAsync("tabela;sqlite;dominio;estado;linhas;malformadas;arquivo_origem");
        foreach (var t in tables) await writer.WriteLineAsync($"{t.OriginalName};{t.SqliteName};{t.Domain};{t.State};{t.RowCount};{t.MalformedRows};{t.SourcePath}");
    }

    private static string Sanitize(string value) => new(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
    private static string Q(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
