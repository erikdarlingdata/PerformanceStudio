using System;
using System.Collections.Generic;

namespace PlanViewer.Core.Models;

/// <summary>
/// Formats a WaitRatio value using adapted time-based units instead of percentages.
/// WaitRatio is expressed in seconds-of-wait per second-of-wall-clock.
///   - Below 1 s/sec → display as ms/sec  (e.g. "320 ms/sec")
///   - 1 to 60 s/sec → display as s/sec   (e.g. "4.2 s/sec")
///   - Above 60 s/sec → display as min/sec (e.g. "1.5 min/sec")
/// </summary>
public static class WaitRatioFormatter
{
    public static string Format(double waitRatio)
    {
        if (waitRatio < 0) waitRatio = 0;

        if (waitRatio < 1.0)
        {
            var ms = waitRatio * 1000.0;
            return ms < 10 ? $"{ms:N1} ms/sec" : $"{ms:N0} ms/sec";
        }
        if (waitRatio < 60.0)
        {
            return $"{waitRatio:N1} s/sec";
        }
        var min = waitRatio / 60.0;
        return $"{min:N1} min/sec";
    }
}

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
