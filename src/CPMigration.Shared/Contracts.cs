using System.Text.Json.Serialization;

namespace CPMigration.Shared;

public enum Severity { Info, Warning, Error, Fatal }
public enum TableState { Mapped, PartiallyMapped, Empty, Technical, Temporary, Backup, Unidentified, ReviewRequired }

public sealed record PipelineOptions(
    string InputDirectory,
    string OutputDirectory,
    int BatchSize = 2_000,
    int RelationshipSampleSize = 5_000,
    double MinimumRelationshipConfidence = 0.70,
    bool ExportRawTables = true);

public sealed record PipelineIssue(
    Severity Severity,
    string Code,
    string Message,
    string? Source = null,
    long? RowNumber = null,
    DateTimeOffset? CreatedAt = null)
{
    public DateTimeOffset Timestamp { get; init; } = CreatedAt ?? DateTimeOffset.Now;
}

public sealed class ColumnProfile
{
    public required string Name { get; init; }
    public string InferredType { get; set; } = "TEXT";
    public long NonEmptyCount { get; set; }
    public long NullCount { get; set; }
    public long DistinctCount { get; set; }
    public int MaxLength { get; set; }
    public List<string> Examples { get; } = [];
    public bool IsPrimaryKeyCandidate { get; set; }
    public bool IsForeignKeyCandidate { get; set; }
}

public sealed class TableProfile
{
    public required string OriginalName { get; init; }
    public required string SqliteName { get; init; }
    public required string SourcePath { get; init; }
    public string Domain { get; set; } = "NAO_IDENTIFICADO";
    public TableState State { get; set; } = TableState.Unidentified;
    public long RowCount { get; set; }
    public long MalformedRows { get; set; }
    public string Encoding { get; set; } = "utf-8";
    public char Delimiter { get; set; } = ';';
    public List<ColumnProfile> Columns { get; } = [];
}

public sealed record RelationshipCandidate(
    string ParentTable,
    string ParentColumn,
    string ChildTable,
    string ChildColumn,
    double NameScore,
    double ValueOverlap,
    double Confidence,
    string Evidence);

public sealed class PipelineSummary
{
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; set; }
    public long ImportedRows { get; set; }
    public int ImportedTables { get; set; }
    public int Relationships { get; set; }
    public int Domains { get; set; }
    public int ExportedTables { get; set; }
    public List<PipelineIssue> Issues { get; } = [];
}

public interface IPipelineLogger
{
    void Write(Severity severity, string code, string message, string? source = null, long? row = null);
}

public sealed class FilePipelineLogger : IPipelineLogger, IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _sync = new();

    public FilePipelineLogger(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(path, append: false) { AutoFlush = true };
    }

    public void Write(Severity severity, string code, string message, string? source = null, long? row = null)
    {
        var line = $"{DateTimeOffset.Now:O} [{severity.ToString().ToUpperInvariant()}] {code} {message}";
        if (!string.IsNullOrWhiteSpace(source)) line += $" | {source}";
        if (row.HasValue) line += $" | linha {row.Value}";
        lock (_sync)
        {
            Console.WriteLine(line);
            _writer.WriteLine(line);
        }
    }

    public void Dispose() => _writer.Dispose();
}
