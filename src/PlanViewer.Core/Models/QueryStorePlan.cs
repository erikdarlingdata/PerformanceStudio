using System;
using System.Globalization;

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

    public string? QueryTextSearch { get; set; }

    public long? MinExecutions { get; set; }
    public long? MinDurationMs { get; set; }
    public long? MinCpuMs { get; set; }
    public string? QueryTextSearchNot { get; set; }
    public bool EscapeBrackets { get; set; }
    public long[]? IncludeQueryIds { get; set; }
    public long[]? IgnoreQueryIds { get; set; }
    public long[]? IncludePlanIds { get; set; }
    public long[]? IgnorePlanIds { get; set; }

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

    /// <summary>
    /// Builds the LIKE pattern for a query-text search, mirroring sp_QuickieStore's
    /// @query_text_search default behavior: trims, then wraps the term in leading/
    /// trailing % wildcards when not already present. When <paramref name="escapeBrackets"/>
    /// is true, escapes [ ] _ with a backslash for use with ESCAPE '\' (so they match
    /// literally); % always stays a LIKE wildcard. The default (false) preserves phase-1
    /// behavior: %, _, [, ] all remain LIKE wildcards (sp_QuickieStore's default
    /// @escape_brackets = 0). Returns null for null/whitespace input (no filter),
    /// matching ParseExecutionType's contract.
    /// </summary>
    public static string? BuildQueryTextSearchPattern(string? input, bool escapeBrackets = false)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim();
        if (!s.StartsWith('%')) s = "%" + s;
        if (!s.EndsWith('%')) s += "%";
        if (escapeBrackets)
            s = s.Replace("[", "\\[").Replace("]", "\\]").Replace("_", "\\_");
        return s;
    }

    /// <summary>
    /// Parses a comma-separated id list into distinct ids (first-seen order).
    /// Null/whitespace/all-empty input returns null (no filter). Empty tokens from
    /// leading/trailing/doubled commas are skipped. Any non-integer token (including
    /// overflow) throws ArgumentException naming the token.
    /// </summary>
    public static long[]? ParseIdList(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var result = new List<long>();
        var seen = new HashSet<long>();
        foreach (var raw in input.Split(','))
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;
            if (!long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                throw new ArgumentException($"Invalid id '{t}' in list. Expected comma-separated integers.");
            if (seen.Add(id)) result.Add(id);
        }
        return result.Count > 0 ? result.ToArray() : null;
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
