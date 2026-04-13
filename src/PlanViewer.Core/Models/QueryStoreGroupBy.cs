namespace PlanViewer.Core.Models;

/// <summary>
/// Specifies how Query Store grid results are grouped.
/// </summary>
public enum QueryStoreGroupBy
{
    /// <summary>No grouping — one row per plan (existing behaviour).</summary>
    None,
    /// <summary>Group by query_hash → plan_hash → query_id/plan_id.</summary>
    QueryHash,
    /// <summary>Group by module → query_hash → query_id/plan_id.</summary>
    Module,
}

/// <summary>
/// A row returned by the grouped query. Contains raw totals for app-side aggregation.
/// Used as the leaf or intermediate row in the hierarchy; parent-level aggregation
/// (SUM totals, recomputed averages) is done on the application side.
/// </summary>
public class QueryStoreGroupedPlanRow
{
    // Grouping keys
    public string ModuleName { get; set; } = "";
    public string QueryHash { get; set; } = "";
    public string QueryPlanHash { get; set; } = "";
    public long QueryId { get; set; }
    public long PlanId { get; set; }
    public string QueryText { get; set; } = "";
    public string PlanXml { get; set; } = "";

    // Raw totals (aggregated across intervals for this plan_id / plan_hash level)
    public long CountExecutions { get; set; }
    public long TotalCpuTimeUs { get; set; }
    public long TotalDurationUs { get; set; }
    public long TotalLogicalIoReads { get; set; }
    public long TotalLogicalIoWrites { get; set; }
    public long TotalPhysicalIoReads { get; set; }
    public long TotalMemoryGrantPages { get; set; }
    public DateTime LastExecutedUtc { get; set; }

    /// <summary>
    /// Indicates whether this row is the "top" (true) or "bottom" (false) representative
    /// for a query_hash/plan_hash pair. Only meaningful for leaf-level (QueryId/PlanId) rows.
    /// </summary>
    public bool IsTopRepresentative { get; set; }
}

/// <summary>
/// Complete result of a grouped fetch. Contains intermediate-level rows (per plan_hash)
/// and leaf-level rows (per query_id/plan_id).
/// </summary>
public class QueryStoreGroupedResult
{
    /// <summary>
    /// Intermediate rows: one per (query_hash, plan_hash) or (module, query_hash).
    /// These carry the aggregated metrics for that group.
    /// </summary>
    public List<QueryStoreGroupedPlanRow> IntermediateRows { get; set; } = new();

    /// <summary>
    /// Leaf rows: top and bottom query_id/plan_id representatives per group.
    /// </summary>
    public List<QueryStoreGroupedPlanRow> LeafRows { get; set; } = new();
}
