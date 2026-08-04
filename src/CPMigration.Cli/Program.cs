using System.Diagnostics;
using System.Text.Json;
using CPMigration.Core;
using CPMigration.Migration;
using CPMigration.ReverseEngineering;
using CPMigration.Shared;

var root = Directory.GetCurrentDirectory();
var input = GetArg("--input") ?? Path.Combine(root, "input");
var output = GetArg("--output") ?? Path.Combine(root, "output");
Directory.CreateDirectory(input);
Directory.CreateDirectory(output);

using var logger = new FilePipelineLogger(Path.Combine(output, "pipeline.log"));
var summary = new PipelineSummary();
var watch = Stopwatch.StartNew();

try
{
    logger.Write(Severity.Info, "PIPELINE_START", "CP Migration iniciado");
    if (!Directory.EnumerateFiles(input, "*.csv", SearchOption.AllDirectories).Any())
        throw new InvalidOperationException($"Nenhum CSV encontrado em: {input}");

    var options = new PipelineOptions(input, output);
    var databasePath = Path.Combine(output, "erp_intermediario.sqlite");

    logger.Write(Severity.Info, "STEP_1", "Importando todos os CSVs para SQLite");
    var importer = new SqliteImportService(logger);
    var tables = await importer.ImportDirectoryAsync(options);
    summary.ImportedTables = tables.Count;
    summary.ImportedRows = tables.Sum(t => t.RowCount);

    logger.Write(Severity.Info, "STEP_2", "Executando engenharia reversa e descoberta de relacionamentos");
    var reverse = new ReverseEngineeringService(logger);
    var relationships = await reverse.AnalyzeAsync(databasePath, tables, output);
    summary.Relationships = relationships.Count;
    summary.Domains = tables.Select(t => t.Domain).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    var migrationPath = Path.Combine(output, "Migration");
    logger.Write(Severity.Info, "STEP_3", "Exportando integralmente todas as tabelas por domínio");
    var exporter = new RawMigrationExporter(logger);
    await exporter.ExportAsync(databasePath, tables, migrationPath);
    summary.ExportedTables = tables.Count;

    logger.Write(Severity.Info, "STEP_4", "Consolidando todos os domínios detectados");
    var consolidator = new DomainConsolidationService(logger);
    var consolidation = await consolidator.ConsolidateAsync(databasePath, tables, relationships, migrationPath);

    logger.Write(Severity.Info, "STEP_5", "Validando chaves, duplicidades e referências órfãs");
    var validator = new IntegrityValidationService(logger);
    var validation = await validator.ValidateAsync(databasePath, tables, relationships, migrationPath);

    summary.FinishedAt = DateTimeOffset.Now;
    var finalSummary = new
    {
        pipeline = summary,
        consolidation,
        validation = new
        {
            findings = validation.Count,
            warnings = validation.Count(x => x.Severity == "WARNING"),
            errors = validation.Count(x => x.Severity == "ERROR")
        },
        elapsed = watch.Elapsed.ToString(@"hh\:mm\:ss"),
        database = databasePath,
        migration = migrationPath
    };
    await File.WriteAllTextAsync(Path.Combine(output, "resumo.json"), JsonSerializer.Serialize(finalSummary, new JsonSerializerOptions { WriteIndented = true }));
    logger.Write(Severity.Info, "PIPELINE_DONE", $"Concluído em {watch.Elapsed:hh\\:mm\\:ss}. Tabelas: {summary.ImportedTables}; registros: {summary.ImportedRows}; relações: {summary.Relationships}; documentos consolidados: {consolidation.Documents}; revisão: {consolidation.ReviewRecords}; validações: {validation.Count}");
    Console.WriteLine($"Resultado: {migrationPath}");
    Environment.ExitCode = 0;
}
catch (Exception ex)
{
    logger.Write(Severity.Fatal, "PIPELINE_FATAL", ex.ToString());
    summary.Issues.Add(new PipelineIssue(Severity.Fatal, "PIPELINE_FATAL", ex.Message));
    summary.FinishedAt = DateTimeOffset.Now;
    await File.WriteAllTextAsync(Path.Combine(output, "resumo.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    Environment.ExitCode = 1;
}

string? GetArg(string name)
{
    var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : null;
}
