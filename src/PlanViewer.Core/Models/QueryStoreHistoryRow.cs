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

    // Display-formatted properties (2 decimal places)
    public string AvgDurationMsDisplay => AvgDurationMs.ToString("N2");
    public string AvgCpuMsDisplay => AvgCpuMs.ToString("N2");
    public string AvgLogicalReadsDisplay => AvgLogicalReads.ToString("N2");
    public string AvgLogicalWritesDisplay => AvgLogicalWrites.ToString("N2");
    public string AvgPhysicalReadsDisplay => AvgPhysicalReads.ToString("N2");
    public string AvgMemoryMbDisplay => AvgMemoryMb.ToString("N2");
    public string AvgRowcountDisplay => AvgRowcount.ToString("N2");
    public string TotalDurationMsDisplay => TotalDurationMs.ToString("N2");
    public string TotalCpuMsDisplay => TotalCpuMs.ToString("N2");
    public string TotalLogicalReadsDisplay => TotalLogicalReads.ToString("N2");
    public string TotalLogicalWritesDisplay => TotalLogicalWrites.ToString("N2");
    public string TotalPhysicalReadsDisplay => TotalPhysicalReads.ToString("N2");

    public string IntervalStartLocal => TimeDisplayHelper.FormatForDisplay(IntervalStartUtc);
    public string LastExecutionLocal => LastExecutionUtc.HasValue ? TimeDisplayHelper.FormatForDisplay(LastExecutionUtc.Value) : "";
}
