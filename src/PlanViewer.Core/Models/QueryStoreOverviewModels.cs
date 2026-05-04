using System;
using System.Collections.Generic;

namespace PlanViewer.Core.Models;

/// <summary>
/// Query Store state for a single database.
/// </summary>
public enum QueryStoreState
{
    Off,
    ReadOnly,
    ReadWrite
}

/// <summary>
/// Query Store state info for a single database.
/// </summary>
public class DatabaseQueryStoreState
{
    public string DatabaseName { get; set; } = "";
    public QueryStoreState State { get; set; }
}

/// <summary>
/// Aggregated metrics for a single database over a time range.
/// </summary>
public class DatabaseMetrics
{
    public string DatabaseName { get; set; } = "";
    public double TotalCpu { get; set; }
    public double TotalDuration { get; set; }
    public long TotalExecutions { get; set; }
    public double TotalReads { get; set; }
    public double TotalWrites { get; set; }
    public double TotalPhysicalReads { get; set; }
    public double TotalMemory { get; set; }

    public double AvgCpu => TotalExecutions > 0 ? TotalCpu / TotalExecutions : 0;
    public double AvgDuration => TotalExecutions > 0 ? TotalDuration / TotalExecutions : 0;
    public double AvgReads => TotalExecutions > 0 ? TotalReads / TotalExecutions : 0;
    public double AvgWrites => TotalExecutions > 0 ? TotalWrites / TotalExecutions : 0;
    public double AvgPhysicalReads => TotalExecutions > 0 ? TotalPhysicalReads / TotalExecutions : 0;
    public double AvgMemory => TotalExecutions > 0 ? TotalMemory / TotalExecutions : 0;
}

/// <summary>
/// Time slice data tagged with the source database name.
/// </summary>
public class DatabaseTimeSlice
{
    public string DatabaseName { get; set; } = "";
    public DateTime IntervalStartUtc { get; set; }
    public double TotalCpu { get; set; }
    public double TotalDuration { get; set; }
    public double TotalReads { get; set; }
    public double TotalWrites { get; set; }
    public double TotalPhysicalReads { get; set; }
    public double TotalMemory { get; set; }
    public long TotalExecutions { get; set; }
}

/// <summary>
/// Wait stats time slice tagged with the source database name.
/// </summary>
public class DatabaseWaitCategoryTimeSlice
{
    public string DatabaseName { get; set; } = "";
    public DateTime IntervalStartUtc { get; set; }
    public int WaitCategory { get; set; }
    public string WaitCategoryDesc { get; set; } = "";
    public double WaitRatio { get; set; }
}
