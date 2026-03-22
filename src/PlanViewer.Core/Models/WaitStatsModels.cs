using System;
using System.Collections.Generic;

namespace PlanViewer.Core.Models;

/// <summary>
/// A single wait category aggregated over a time range.
/// WaitRatio = SUM(total_query_wait_time_ms) / interval_ms.
/// </summary>
public class WaitCategoryTotal
{
    public int WaitCategory { get; set; }
    public string WaitCategoryDesc { get; set; } = "";
    public double WaitRatio { get; set; }
}

/// <summary>
/// One hourly bucket of wait stats for the ribbon chart.
/// WaitRatio = SUM(total_query_wait_time_ms) / 3_600_000.
/// </summary>
public class WaitCategoryTimeSlice
{
    public DateTime IntervalStartUtc { get; set; }
    public int WaitCategory { get; set; }
    public string WaitCategoryDesc { get; set; } = "";
    public double WaitRatio { get; set; }
}

/// <summary>
/// Processed wait profile ready for display.
/// Top 3 categories are named; the rest are consolidated into "Others".
/// </summary>
public class WaitProfile
{
    public List<WaitProfileSegment> Segments { get; set; } = new();
    public double GrandTotalRatio { get; set; }
}

public class WaitProfileSegment
{
    public string Category { get; set; } = "";
    public double WaitRatio { get; set; }
    /// <summary>Fraction of the grand total [0..1].</summary>
    public double Ratio { get; set; }
    /// <summary>True for one of the top-3 named categories, false for "Others".</summary>
    public bool IsNamed { get; set; }
}
