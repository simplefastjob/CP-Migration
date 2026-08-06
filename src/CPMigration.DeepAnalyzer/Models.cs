namespace CPMigration.DeepAnalyzer;

public sealed class DatabaseAnalysis
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.Now;
    public string DatabasePath { get; init; } = "";
    public long DatabaseSizeBytes { get; init; }
    public int TableCount => Tables.Count;
    public long TotalRows => Tables.Sum(x => x.RowCount);
    public List<TableAnalysis> Tables { get; init; } = [];
    public List<AnalysisIssue> Issues { get; init; } = [];
}

public sealed class TableAnalysis
{
    public string Name { get; init; } = "";
    public long RowCount { get; set; }
    public string Sql { get; init; } = "";
    public string SuggestedDomain { get; set; } = "NAO_IDENTIFICADO";
    public double DomainConfidence { get; set; }
    public List<ColumnAnalysis> Columns { get; init; } = [];
    public List<ForeignKeyAnalysis> ForeignKeys { get; init; } = [];
    public List<IndexAnalysis> Indexes { get; init; } = [];
}

public sealed class ColumnAnalysis
{
    public int Ordinal { get; init; }
    public string Name { get; init; } = "";
    public string DeclaredType { get; init; } = "";
    public bool NotNull { get; init; }
    public bool PrimaryKey { get; init; }
    public string? DefaultValue { get; init; }
    public long NonNullCount { get; set; }
    public long DistinctCount { get; set; }
    public int MaxLength { get; set; }
    public string SuggestedMeaning { get; set; } = "DESCONHECIDO";
    public double MeaningConfidence { get; set; }
    public List<string> Examples { get; init; } = [];
}

public sealed record ForeignKeyAnalysis(string FromColumn, string TargetTable, string TargetColumn, string OnUpdate, string OnDelete);
public sealed record IndexAnalysis(string Name, bool Unique, IReadOnlyList<string> Columns);
public sealed record AnalysisIssue(string Severity, string Code, string Message, string? Table = null, string? Column = null);
