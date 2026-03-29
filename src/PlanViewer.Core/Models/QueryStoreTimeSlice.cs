using System;

namespace PlanViewer.Core.Models;

/// <summary>
/// One hourly bucket of aggregated Query Store metrics, used by the time-range slicer.
/// </summary>
public class QueryStoreTimeSlice
{
    public DateTime IntervalStartUtc { get; set; }
    public double TotalCpu { get; set; }
    public double TotalDuration { get; set; }
    public double TotalReads { get; set; }
    public double TotalWrites { get; set; }
    public double TotalPhysicalReads { get; set; }
    public double TotalMemory { get; set; }
    public long TotalExecutions { get; set; }

    public double AvgCpu => TotalExecutions > 0 ? TotalCpu / TotalExecutions : 0;
    public double AvgDuration => TotalExecutions > 0 ? TotalDuration / TotalExecutions : 0;
    public double AvgReads => TotalExecutions > 0 ? TotalReads / TotalExecutions : 0;
    public double AvgWrites => TotalExecutions > 0 ? TotalWrites / TotalExecutions : 0;
    public double AvgPhysicalReads => TotalExecutions > 0 ? TotalPhysicalReads / TotalExecutions : 0;
    public double AvgMemory => TotalExecutions > 0 ? TotalMemory / TotalExecutions : 0;
}
