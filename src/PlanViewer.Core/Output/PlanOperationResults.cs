using System.Text.Json.Serialization;

namespace PlanViewer.Core.Output;


public sealed class PlanCloseResult
{
    [JsonPropertyName("session_id")]
    public string SessionId { get; init; } = "";

    [JsonPropertyName("closed")]
    public bool Closed { get; init; }
}

public sealed class PlanSummaryResult
{
    [JsonPropertyName("session_id")]
    public string SessionId { get; init; } = "";

    [JsonPropertyName("label")]
    public string Label { get; init; } = "";

    [JsonPropertyName("source")]
    public string Source { get; init; } = "";

    [JsonPropertyName("total_statements")]
    public int TotalStatements { get; init; }

    [JsonPropertyName("total_warnings")]
    public int TotalWarnings { get; init; }

    [JsonPropertyName("critical_warnings")]
    public int CriticalWarnings { get; init; }

    [JsonPropertyName("missing_indexes")]
    public int MissingIndexes { get; init; }

    [JsonPropertyName("has_actual_stats")]
    public bool HasActualStats { get; init; }

    [JsonPropertyName("max_estimated_cost")]
    public double MaxEstimatedCost { get; init; }

    [JsonPropertyName("warning_types")]
    public IReadOnlyList<string> WarningTypes { get; init; } = [];
}


public sealed class PlanWarningsResult
{
    [JsonPropertyName("session_id")]
    public string SessionId { get; init; } = "";

    [JsonPropertyName("warning_count")]
    public int WarningCount { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<PlanWarningItem> Warnings { get; init; } = [];
}

public sealed class PlanWarningItem
{
    [JsonPropertyName("severity")]
    public string Severity { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    [JsonPropertyName("node_id")]
    public int? NodeId { get; init; }

    [JsonPropertyName("operator")]
    public string? Operator { get; init; }

    [JsonPropertyName("statement")]
    public string Statement { get; init; } = "";
}


public sealed class ExpensiveOperatorsResult
{
    [JsonPropertyName("session_id")]
    public string SessionId { get; init; } = "";

    [JsonPropertyName("ranked_by")]
    public string RankedBy { get; init; } = "";

    [JsonPropertyName("operators")]
    public IReadOnlyList<ExpensiveOperatorItem> Operators { get; init; } = [];
}

public sealed class ExpensiveOperatorItem
{
    [JsonPropertyName("node_id")]
    public int NodeId { get; init; }

    [JsonPropertyName("physical_op")]
    public string PhysicalOp { get; init; } = "";

    [JsonPropertyName("logical_op")]
    public string LogicalOp { get; init; } = "";

    [JsonPropertyName("cost_percent")]
    public int CostPercent { get; init; }

    [JsonPropertyName("estimated_rows")]
    public double EstimatedRows { get; init; }

    [JsonPropertyName("actual_rows")]
    public long ActualRows { get; init; }

    [JsonPropertyName("actual_elapsed_ms")]
    public long ActualElapsedMs { get; init; }

    [JsonPropertyName("actual_cpu_ms")]
    public long ActualCpuMs { get; init; }

    [JsonPropertyName("logical_reads")]
    public long LogicalReads { get; init; }

    [JsonPropertyName("physical_reads")]
    public long PhysicalReads { get; init; }

    [JsonPropertyName("object_name")]
    public string? ObjectName { get; init; }

    [JsonPropertyName("statement")]
    public string Statement { get; init; } = "";
}


public sealed class MissingIndexesResult
{
    [JsonPropertyName("session_id")]
    public string SessionId { get; init; } = "";

    [JsonPropertyName("missing_index_count")]
    public int MissingIndexCount { get; init; }

    [JsonPropertyName("indexes")]
    public IReadOnlyList<MissingIndexItem> Indexes { get; init; } = [];
}

public sealed class MissingIndexItem
{
    [JsonPropertyName("database")]
    public string? Database { get; init; }

    [JsonPropertyName("schema_name")]
    public string? SchemaName { get; init; }

    [JsonPropertyName("table")]
    public string Table { get; init; } = "";

    [JsonPropertyName("impact")]
    public double Impact { get; init; }

    [JsonPropertyName("equality_columns")]
    public IReadOnlyList<string> EqualityColumns { get; init; } = [];

    [JsonPropertyName("inequality_columns")]
    public IReadOnlyList<string> InequalityColumns { get; init; } = [];

    [JsonPropertyName("include_columns")]
    public IReadOnlyList<string> IncludeColumns { get; init; } = [];

    [JsonPropertyName("create_statement")]
    public string CreateStatement { get; init; } = "";
}
