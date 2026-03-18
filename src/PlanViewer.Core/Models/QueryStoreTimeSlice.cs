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
}
