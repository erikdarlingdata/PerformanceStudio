using System;

namespace PlanViewer.Core.Models;

/// <summary>
/// Server-side search filter for Query Store fetches.
/// All properties are optional — only non-null values generate WHERE clauses.
/// </summary>
public class QueryStoreFilter
{
    public long? QueryId { get; set; }
    public long? PlanId { get; set; }
    public string? QueryHash { get; set; }
    public string? QueryPlanHash { get; set; }
    public string? ModuleName { get; set; }
}

public class QueryStorePlan
{
    public long QueryId { get; set; }
    public long PlanId { get; set; }
    public string QueryHash { get; set; } = "";
    public string QueryPlanHash { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public string QueryText { get; set; } = "";
    public string PlanXml { get; set; } = "";

    // Averages (per execution)
    public double AvgCpuTimeUs { get; set; }
    public double AvgDurationUs { get; set; }
    public double AvgLogicalIoReads { get; set; }
    public double AvgLogicalIoWrites { get; set; }
    public double AvgPhysicalIoReads { get; set; }
    public double AvgMemoryGrantPages { get; set; }

    // Totals (avg * executions, aggregated across intervals)
    public long CountExecutions { get; set; }
    public long TotalCpuTimeUs { get; set; }
    public long TotalDurationUs { get; set; }
    public long TotalLogicalIoReads { get; set; }
    public long TotalLogicalIoWrites { get; set; }
    public long TotalPhysicalIoReads { get; set; }
    public long TotalMemoryGrantPages { get; set; }

    // Query Store stores times in UTC
    public DateTime LastExecutedUtc { get; set; }
}
