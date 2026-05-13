using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

/// <summary>
/// Post-parse analysis pass that walks a parsed plan tree and adds warnings
/// for common performance anti-patterns. Called after ShowPlanParser.Parse().
/// </summary>
public static partial class PlanAnalyzer
{
    private static readonly Regex FunctionInPredicateRegex = new(
        @"\b(CONVERT_IMPLICIT|CONVERT|CAST|isnull|coalesce|datepart|datediff|dateadd|year|month|day|upper|lower|ltrim|rtrim|trim|substring|left|right|charindex|replace|len|datalength|abs|floor|ceiling|round|reverse|stuff|format)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LeadingWildcardLikeRegex = new(
        @"\blike\b[^'""]*?N?'%",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CaseInPredicateRegex = new(
        @"\bCASE\s+(WHEN\b|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static void Analyze(ParsedPlan plan, AnalyzerConfig? config = null, ServerMetadata? serverMetadata = null)
    {
        var cfg = config ?? AnalyzerConfig.Default;
        foreach (var batch in plan.Batches)
        {
            foreach (var stmt in batch.Statements)
            {
                AnalyzeStatement(stmt, cfg, serverMetadata);

                if (stmt.RootNode != null)
                    AnalyzeNodeTree(stmt.RootNode, stmt, cfg);

                MarkLegacyWarnings(stmt);
            }
        }

        // Apply severity overrides to all warnings
        if (cfg.Rules?.SeverityOverrides?.Count > 0)
            ApplySeverityOverrides(plan, cfg);
    }

    /// <summary>
    /// Rule types that predate the benefit-scoring framework (#215) and haven't
    /// been folded into A/B/C/D categorization yet. Tagged so reviewers can hold
    /// new-framework items to a higher bar vs known-legacy items that will be
    /// reworked later. Remove entries from this set as rules migrate.
    /// </summary>
    private static readonly HashSet<string> LegacyWarningTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Excessive Memory Grant",
        "Large Memory Grant",
        "Compile Memory Exceeded",
        "Local Variables",
        "Optimize For Unknown",
        "Low Impact Index",
        "Wide Index Suggestion",
        "Duplicate Index Suggestions",
        "Table Variable",
        "Scalar UDF",
        "Parallel Skew",
        "Estimated Plan CE Guess",
        "Data Type Mismatch",
        "Lazy Spool Ineffective",
        "Join OR Clause",
        "Many-to-Many Merge Join",
        "Table-Valued Function",
        "Top Above Scan",
        "Row Goal",
        "NOT IN with Nullable Column",
        "Implicit Conversion",
    };


    // Rule number → WarningType mapping for severity overrides
    private static readonly Dictionary<int, string> RuleWarningTypes = new()
    {
        [1] = "Filter Operator", [2] = "Eager Index Spool", [3] = "Serial Plan",
        [4] = "UDF Execution", [5] = "Row Estimate Mismatch", [6] = "Scalar UDF",
        [7] = "Spill", [8] = "Parallel Skew", [9] = "Memory Grant",
        [10] = "Key Lookup", [11] = "Scan With Predicate", [12] = "Non-SARGable Predicate",
        [13] = "Data Type Mismatch", [14] = "Lazy Spool Ineffective", [15] = "Join OR Clause",
        [16] = "Nested Loops High Executions", [17] = "Many-to-Many Merge Join",
        [18] = "Compile Memory Exceeded", [19] = "High Compile CPU", [20] = "Local Variables",
        [22] = "Table Variable", [23] = "Table-Valued Function",
        [24] = "Top Above Scan", [25] = "Ineffective Parallelism", [26] = "Row Goal",
        [27] = "Optimize For Unknown", [28] = "NOT IN with Nullable Column",
        [29] = "Implicit Conversion", [30] = "Wide Index Suggestion",
        [31] = "Parallel Wait Bottleneck",
        [32] = "Scan Cardinality Misestimate",
        [33] = "Estimated Plan CE Guess",
        [38] = "Standard Edition DOP Limitation"
    };

    // Reverse lookup: WarningType → rule number
    private static readonly Dictionary<string, int> WarningTypeToRule;

    static PlanAnalyzer()
    {
        WarningTypeToRule = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rule, type) in RuleWarningTypes)
            WarningTypeToRule[type] = rule;
    }


    /// <summary>
    private record ScanImpact(double CostPct, double ElapsedPct, string? Summary);


}
