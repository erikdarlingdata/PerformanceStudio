using System;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Models;

public class QueryStoreHistoryRow
{
    public long PlanId { get; set; }
    public string QueryPlanHash { get; set; } = "";
    public DateTime IntervalStartUtc { get; set; }
    public long CountExecutions { get; set; }

    public double AvgDurationMs { get; set; }
    public double AvgCpuMs { get; set; }
    public double AvgLogicalReads { get; set; }
    public double AvgLogicalWrites { get; set; }
    public double AvgPhysicalReads { get; set; }
    public double AvgMemoryMb { get; set; }
    public double AvgRowcount { get; set; }

    public double TotalDurationMs { get; set; }
    public double TotalCpuMs { get; set; }
    public double TotalLogicalReads { get; set; }
    public double TotalLogicalWrites { get; set; }
    public double TotalPhysicalReads { get; set; }

    public int MinDop { get; set; }
    public int MaxDop { get; set; }
    public DateTime? LastExecutionUtc { get; set; }

    public string IntervalStartLocal => TimeDisplayHelper.FormatForDisplay(IntervalStartUtc);
    public string LastExecutionLocal => LastExecutionUtc.HasValue ? TimeDisplayHelper.FormatForDisplay(LastExecutionUtc.Value) : "";
}
