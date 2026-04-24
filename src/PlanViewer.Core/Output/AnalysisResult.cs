using System.Text.Json.Serialization;

namespace PlanViewer.Core.Output;

/// <summary>
/// Top-level JSON output from plan analysis. Designed to be consumed by LLMs
/// and CI pipelines — everything needed to understand plan quality without
/// reading raw XML.
/// </summary>
public class AnalysisResult
{
    [JsonPropertyName("plan_source")]
    public string PlanSource { get; set; } = "";

    [JsonPropertyName("sql_server_version")]
    public string? SqlServerVersion { get; set; }

    [JsonPropertyName("sql_server_build")]
    public string? SqlServerBuild { get; set; }

    [JsonPropertyName("statements")]
    public List<StatementResult> Statements { get; set; } = new();

    [JsonPropertyName("summary")]
    public AnalysisSummary Summary { get; set; } = new();

    [JsonPropertyName("server_context")]
    public ServerContextResult? ServerContext { get; set; }
}

public class AnalysisSummary
{
    [JsonPropertyName("total_statements")]
    public int TotalStatements { get; set; }

    [JsonPropertyName("total_warnings")]
    public int TotalWarnings { get; set; }

    [JsonPropertyName("critical_warnings")]
    public int CriticalWarnings { get; set; }

    [JsonPropertyName("missing_indexes")]
    public int MissingIndexes { get; set; }

    [JsonPropertyName("has_actual_stats")]
    public bool HasActualStats { get; set; }

    [JsonPropertyName("max_estimated_cost")]
    public double MaxEstimatedCost { get; set; }

    [JsonPropertyName("warning_types")]
    public List<string> WarningTypes { get; set; } = new();
}

public class StatementResult
{
    [JsonPropertyName("statement_text")]
    public string StatementText { get; set; } = "";

    [JsonPropertyName("statement_type")]
    public string StatementType { get; set; } = "";

    [JsonPropertyName("estimated_cost")]
    public double EstimatedCost { get; set; }

    [JsonPropertyName("estimated_rows")]
    public double EstimatedRows { get; set; }

    // Compilation metadata
    [JsonPropertyName("optimization_level")]
    public string? OptimizationLevel { get; set; }

    [JsonPropertyName("early_abort_reason")]
    public string? EarlyAbortReason { get; set; }

    [JsonPropertyName("cardinality_estimation_model")]
    public int CardinalityEstimationModel { get; set; }

    [JsonPropertyName("compile_time_ms")]
    public long CompileTimeMs { get; set; }

    [JsonPropertyName("compile_memory_kb")]
    public long CompileMemoryKB { get; set; }

    [JsonPropertyName("cached_plan_size_kb")]
    public long CachedPlanSizeKB { get; set; }

    // Parallelism
    [JsonPropertyName("degree_of_parallelism")]
    public int DegreeOfParallelism { get; set; }

    [JsonPropertyName("non_parallel_reason")]
    public string? NonParallelReason { get; set; }

    // Hashes for identification
    [JsonPropertyName("query_hash")]
    public string? QueryHash { get; set; }

    [JsonPropertyName("query_plan_hash")]
    public string? QueryPlanHash { get; set; }

    // Memory grant
    [JsonPropertyName("memory_grant")]
    public MemoryGrantResult? MemoryGrant { get; set; }

    // Runtime stats (actual plans only)
    [JsonPropertyName("query_time")]
    public QueryTimeResult? QueryTime { get; set; }

    // Parameters
    [JsonPropertyName("parameters")]
    public List<ParameterResult> Parameters { get; set; } = new();

    // Analysis results
    [JsonPropertyName("warnings")]
    public List<WarningResult> Warnings { get; set; } = new();

    [JsonPropertyName("missing_indexes")]
    public List<MissingIndexResult> MissingIndexes { get; set; } = new();

    // Operator tree
    [JsonPropertyName("operator_tree")]
    public OperatorResult? OperatorTree { get; set; }

    // Plan features
    [JsonPropertyName("plan_guide")]
    public string? PlanGuide { get; set; }

    [JsonPropertyName("query_store_hint")]
    public string? QueryStoreHint { get; set; }

    [JsonPropertyName("trace_flags")]
    public List<string> TraceFlags { get; set; } = new();

    [JsonPropertyName("batch_mode_on_rowstore")]
    public bool BatchModeOnRowStore { get; set; }

    // Wait stats (actual plans only)
    [JsonPropertyName("wait_stats")]
    public List<WaitStatResult> WaitStats { get; set; } = new();

    // Wait stats benefit analysis
    [JsonPropertyName("wait_benefits")]
    public List<WaitBenefitResult> WaitBenefits { get; set; } = new();

    // Cursor metadata
    [JsonPropertyName("cursor")]
    public CursorResult? Cursor { get; set; }
}

public class MemoryGrantResult
{
    [JsonPropertyName("requested_kb")]
    public long RequestedKB { get; set; }

    [JsonPropertyName("granted_kb")]
    public long GrantedKB { get; set; }

    [JsonPropertyName("max_used_kb")]
    public long MaxUsedKB { get; set; }

    [JsonPropertyName("grant_wait_ms")]
    public long GrantWaitMs { get; set; }

    [JsonPropertyName("feedback_adjusted")]
    public string? FeedbackAdjusted { get; set; }

    [JsonPropertyName("estimated_available_memory_grant_kb")]
    public long EstimatedAvailableMemoryGrantKB { get; set; }

    /// <summary>
    /// Optimizer's pre-execution "desired" grant (parallel-adjusted).
    /// Non-zero on estimated plans; pairs with DesiredKB serial-required as fallback
    /// when no runtime-granted memory exists (#215 E6).
    /// </summary>
    [JsonPropertyName("desired_kb")]
    public long DesiredKB { get; set; }

    /// <summary>
    /// Optimizer's pre-execution serial-required grant (memory minimum before DOP scaling).
    /// </summary>
    [JsonPropertyName("serial_required_kb")]
    public long SerialRequiredKB { get; set; }
}

public class QueryTimeResult
{
    [JsonPropertyName("cpu_time_ms")]
    public long CpuTimeMs { get; set; }

    [JsonPropertyName("elapsed_time_ms")]
    public long ElapsedTimeMs { get; set; }

    /// <summary>
    /// Sum of external/preemptive wait time (MEMORY_ALLOCATION_*, PREEMPTIVE_*) —
    /// these waits are CPU-busy in kernel and inflate CpuTimeMs vs real query CPU.
    /// Subtract from CpuTimeMs for a truer CPU:Elapsed ratio.
    /// </summary>
    [JsonPropertyName("external_wait_ms")]
    public long ExternalWaitMs { get; set; }
}

public class ParameterResult
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("data_type")]
    public string DataType { get; set; } = "";

    [JsonPropertyName("compiled_value")]
    public string? CompiledValue { get; set; }

    [JsonPropertyName("runtime_value")]
    public string? RuntimeValue { get; set; }

    [JsonPropertyName("sniffing_issue")]
    public bool SniffingIssue { get; set; }
}

public class WarningResult
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("operator")]
    public string? Operator { get; set; }

    [JsonPropertyName("node_id")]
    public int? NodeId { get; set; }

    [JsonPropertyName("max_benefit_percent")]
    public double? MaxBenefitPercent { get; set; }

    [JsonPropertyName("actionable_fix")]
    public string? ActionableFix { get; set; }

    /// <summary>
    /// True for rules predating the benefit-scoring framework. Renderers show a
    /// "legacy" badge to distinguish from new-framework warnings.
    /// </summary>
    [JsonPropertyName("is_legacy")]
    public bool IsLegacy { get; set; }
}

public class MissingIndexResult
{
    [JsonPropertyName("table")]
    public string Table { get; set; } = "";

    [JsonPropertyName("impact")]
    public double Impact { get; set; }

    [JsonPropertyName("equality_columns")]
    public List<string> EqualityColumns { get; set; } = new();

    [JsonPropertyName("inequality_columns")]
    public List<string> InequalityColumns { get; set; } = new();

    [JsonPropertyName("include_columns")]
    public List<string> IncludeColumns { get; set; } = new();

    [JsonPropertyName("create_statement")]
    public string CreateStatement { get; set; } = "";
}

public class OperatorResult
{
    [JsonPropertyName("node_id")]
    public int NodeId { get; set; }

    [JsonPropertyName("physical_op")]
    public string PhysicalOp { get; set; } = "";

    [JsonPropertyName("logical_op")]
    public string LogicalOp { get; set; } = "";

    [JsonPropertyName("cost_percent")]
    public int CostPercent { get; set; }

    [JsonPropertyName("estimated_rows")]
    public double EstimatedRows { get; set; }

    [JsonPropertyName("estimated_cost")]
    public double EstimatedCost { get; set; }

    [JsonPropertyName("estimated_io")]
    public double EstimatedIO { get; set; }

    [JsonPropertyName("estimated_cpu")]
    public double EstimatedCPU { get; set; }

    [JsonPropertyName("estimated_row_size")]
    public int EstimatedRowSize { get; set; }

    // Object context
    [JsonPropertyName("object_name")]
    public string? ObjectName { get; set; }

    [JsonPropertyName("index_name")]
    public string? IndexName { get; set; }

    [JsonPropertyName("database_name")]
    public string? DatabaseName { get; set; }

    // Predicates — key for understanding what the operator does
    [JsonPropertyName("seek_predicates")]
    public string? SeekPredicates { get; set; }

    [JsonPropertyName("predicate")]
    public string? Predicate { get; set; }

    [JsonPropertyName("output_columns")]
    public string? OutputColumns { get; set; }

    // Join details
    [JsonPropertyName("hash_keys_build")]
    public string? HashKeysBuild { get; set; }

    [JsonPropertyName("hash_keys_probe")]
    public string? HashKeysProbe { get; set; }

    [JsonPropertyName("outer_references")]
    public string? OuterReferences { get; set; }

    // Sort/aggregate
    [JsonPropertyName("order_by")]
    public string? OrderBy { get; set; }

    [JsonPropertyName("group_by")]
    public string? GroupBy { get; set; }

    // Parallelism
    [JsonPropertyName("parallel")]
    public bool Parallel { get; set; }

    [JsonPropertyName("execution_mode")]
    public string? ExecutionMode { get; set; }

    [JsonPropertyName("actual_execution_mode")]
    public string? ActualExecutionMode { get; set; }

    // Actual stats (when available)
    [JsonPropertyName("actual_rows")]
    public long? ActualRows { get; set; }

    [JsonPropertyName("actual_executions")]
    public long? ActualExecutions { get; set; }

    [JsonPropertyName("actual_elapsed_ms")]
    public long? ActualElapsedMs { get; set; }

    [JsonPropertyName("actual_cpu_ms")]
    public long? ActualCpuMs { get; set; }

    [JsonPropertyName("actual_logical_reads")]
    public long? ActualLogicalReads { get; set; }

    [JsonPropertyName("actual_physical_reads")]
    public long? ActualPhysicalReads { get; set; }

    // Warnings on this operator
    [JsonPropertyName("warnings")]
    public List<WarningResult> Warnings { get; set; } = new();

    // Children
    [JsonPropertyName("children")]
    public List<OperatorResult> Children { get; set; } = new();
}

public class WaitStatResult
{
    [JsonPropertyName("wait_type")]
    public string WaitType { get; set; } = "";

    [JsonPropertyName("wait_time_ms")]
    public long WaitTimeMs { get; set; }

    [JsonPropertyName("wait_count")]
    public long WaitCount { get; set; }
}

public class WaitBenefitResult
{
    [JsonPropertyName("wait_type")]
    public string WaitType { get; set; } = "";

    [JsonPropertyName("max_benefit_percent")]
    public double MaxBenefitPercent { get; set; }

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";
}

public class CursorResult
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("actual_type")]
    public string? ActualType { get; set; }

    [JsonPropertyName("requested_type")]
    public string? RequestedType { get; set; }

    [JsonPropertyName("concurrency")]
    public string? Concurrency { get; set; }

    [JsonPropertyName("forward_only")]
    public bool ForwardOnly { get; set; }
}

public class ServerContextResult
{
    [JsonPropertyName("server_name")]
    public string? ServerName { get; set; }

    [JsonPropertyName("product_version")]
    public string? ProductVersion { get; set; }

    [JsonPropertyName("product_level")]
    public string? ProductLevel { get; set; }

    [JsonPropertyName("edition")]
    public string? Edition { get; set; }

    [JsonPropertyName("is_azure")]
    public bool IsAzure { get; set; }

    [JsonPropertyName("cpu_count")]
    public int CpuCount { get; set; }

    [JsonPropertyName("physical_memory_mb")]
    public long PhysicalMemoryMB { get; set; }

    [JsonPropertyName("max_dop")]
    public int MaxDop { get; set; }

    [JsonPropertyName("cost_threshold_for_parallelism")]
    public int CostThresholdForParallelism { get; set; }

    [JsonPropertyName("max_server_memory_mb")]
    public long MaxServerMemoryMB { get; set; }

    [JsonPropertyName("database")]
    public DatabaseContextResult? Database { get; set; }
}

public class DatabaseContextResult
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("compatibility_level")]
    public int CompatibilityLevel { get; set; }

    [JsonPropertyName("collation")]
    public string CollationName { get; set; } = "";

    // Isolation
    [JsonPropertyName("snapshot_isolation_state")]
    public int SnapshotIsolationState { get; set; }

    [JsonPropertyName("read_committed_snapshot")]
    public bool ReadCommittedSnapshot { get; set; }

    // Stats
    [JsonPropertyName("auto_create_stats")]
    public bool AutoCreateStats { get; set; }

    [JsonPropertyName("auto_update_stats")]
    public bool AutoUpdateStats { get; set; }

    [JsonPropertyName("auto_update_stats_async")]
    public bool AutoUpdateStatsAsync { get; set; }

    // Parameterization
    [JsonPropertyName("parameterization_forced")]
    public bool ParameterizationForced { get; set; }

    [JsonPropertyName("non_default_scoped_configs")]
    public List<ScopedConfigResult> NonDefaultScopedConfigs { get; set; } = new();
}

public class ScopedConfigResult
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("value_for_secondary")]
    public string? ValueForSecondary { get; set; }
}
