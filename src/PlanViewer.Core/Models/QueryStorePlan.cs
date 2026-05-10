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
    /// <summary>
    /// One or more execution_type_desc values to filter by.
    /// Single value → equality predicate; multiple values (e.g. "Aborted","Exception" for "Failed") → IN predicate.
    /// </summary>
    public string[]? ExecutionTypeDescs { get; set; }

    /// <summary>
    /// Parses a user-friendly execution-type string into the matching SQL execution_type_desc values.
    /// Accepts (case-insensitive): regular, aborted, exception, failed (= aborted + exception), any.
    /// Returns null when input is null, empty, or "any". Throws ArgumentException for unknown values.
    /// </summary>
    public static string[]? ParseExecutionType(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        return input.Trim().ToLowerInvariant() switch
        {
            "any" => null,
            "regular" => ["Regular"],
            "aborted" => ["Aborted"],
            "exception" => ["Exception"],
            "failed" => ["Aborted", "Exception"],
            _ => throw new ArgumentException(
                $"Unknown execution type '{input}'. Valid values: regular, aborted, exception, failed, any."),
        };
    }
}

public class QueryStorePlan
{
    public long QueryId { get; set; }
    public long PlanId { get; set; }
    public string QueryHash { get; set; } = "";
    public string QueryPlanHash { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public string ExecutionTypeDesc { get; set; } = "";
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
