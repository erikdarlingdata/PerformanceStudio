using System.IO;

namespace PlanViewer.Core.Output;

public static class ComparisonFormatter
{
    public static string Compare(
        AnalysisResult planA, AnalysisResult planB,
        string labelA, string labelB)
    {
        using var writer = new StringWriter();
        WriteComparison(planA, planB, labelA, labelB, writer);
        return writer.ToString();
    }

    private static void WriteComparison(
        AnalysisResult planA, AnalysisResult planB,
        string labelA, string labelB, TextWriter writer)
    {
        writer.WriteLine("=== Plan Comparison ===");
        writer.WriteLine($"Plan A: {labelA}");
        writer.WriteLine($"Plan B: {labelB}");

        // Note estimated vs actual mismatch
        if (planA.Summary.HasActualStats != planB.Summary.HasActualStats)
        {
            writer.WriteLine();
            var estSide = planA.Summary.HasActualStats ? "Plan B" : "Plan A";
            writer.WriteLine($"Note: {estSide} is an estimated plan. Runtime metrics only available for the actual plan.");
        }

        writer.WriteLine();

        var matches = MatchStatements(planA, planB);

        if (matches.Count == 0)
        {
            writer.WriteLine("No statements to compare.");
            return;
        }

        int matchIndex = 0;
        foreach (var match in matches)
        {
            matchIndex++;

            if (match.A != null && match.B != null)
            {
                WriteStatementComparison(match.A, match.B, matchIndex, writer);
            }
            else if (match.A != null)
            {
                writer.WriteLine($"--- Statement {matchIndex} (only in Plan A) ---");
                writer.WriteLine(TruncateText(match.A.StatementText, 500));
                writer.WriteLine("  (no comparison available)");
                writer.WriteLine();
            }
            else if (match.B != null)
            {
                writer.WriteLine($"--- Statement {matchIndex} (only in Plan B) ---");
                writer.WriteLine(TruncateText(match.B.StatementText, 500));
                writer.WriteLine("  (no comparison available)");
                writer.WriteLine();
            }
        }

    }

    private static void WriteStatementComparison(
        StatementResult a, StatementResult b, int index, TextWriter writer)
    {
        writer.WriteLine($"--- Statement {index} ---");
        writer.WriteLine(TruncateText(a.StatementText, 500));
        writer.WriteLine();

        // Estimated metrics (always available)
        WriteMetricLine(writer, "Estimated cost", a.EstimatedCost, b.EstimatedCost,
            "F4", "", "cheaper", lowerIsBetter: true);
        WriteMetricLine(writer, "Estimated rows", a.EstimatedRows, b.EstimatedRows,
            "N0", "", "fewer", lowerIsBetter: true);

        // Runtime (actual plans only)
        if (a.QueryTime != null || b.QueryTime != null)
        {
            WriteMetricLine(writer, "Runtime",
                a.QueryTime?.ElapsedTimeMs, b.QueryTime?.ElapsedTimeMs,
                "N0", "ms", "faster", lowerIsBetter: true);
            WriteMetricLine(writer, "CPU time",
                a.QueryTime?.CpuTimeMs, b.QueryTime?.CpuTimeMs,
                "N0", "ms", "faster", lowerIsBetter: true);
        }

        // I/O from operator tree
        var (aLR, aPR) = SumTreeIO(a.OperatorTree);
        var (bLR, bPR) = SumTreeIO(b.OperatorTree);
        if (aLR > 0 || bLR > 0)
            WriteMetricLine(writer, "Logical reads", aLR, bLR, "N0", "", "fewer", lowerIsBetter: true);
        if (aPR > 0 || bPR > 0)
            WriteMetricLine(writer, "Physical reads", aPR, bPR, "N0", "", "fewer", lowerIsBetter: true);

        // Memory grant
        if ((a.MemoryGrant != null && a.MemoryGrant.GrantedKB > 0) ||
            (b.MemoryGrant != null && b.MemoryGrant.GrantedKB > 0))
        {
            var aGrantMB = a.MemoryGrant != null ? a.MemoryGrant.GrantedKB / 1024.0 : 0;
            var bGrantMB = b.MemoryGrant != null ? b.MemoryGrant.GrantedKB / 1024.0 : 0;
            WriteMetricLine(writer, "Memory grant", aGrantMB, bGrantMB, "N1", " MB", "less", lowerIsBetter: true);
        }

        // DOP — show raw values, no percentage
        if (a.DegreeOfParallelism > 0 || b.DegreeOfParallelism > 0)
        {
            writer.WriteLine($"  DOP:                {a.DegreeOfParallelism} -> {b.DegreeOfParallelism}");
        }

        // Warning and missing index counts
        WriteCountDelta(writer, "Warnings", a.Warnings.Count, b.Warnings.Count);
        WriteCountDelta(writer, "Missing indexes", a.MissingIndexes.Count, b.MissingIndexes.Count);

        // Wait stats
        if (a.WaitStats.Count > 0 || b.WaitStats.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("  Wait stats:");
            if (a.WaitStats.Count > 0)
            {
                writer.WriteLine("    Plan A:");
                foreach (var w in a.WaitStats.OrderByDescending(w => w.WaitTimeMs))
                    writer.WriteLine($"      - {w.WaitType} {w.WaitTimeMs:N0}ms");
            }
            if (a.WaitStats.Count > 0 && b.WaitStats.Count > 0)
                writer.WriteLine();
            if (b.WaitStats.Count > 0)
            {
                writer.WriteLine("    Plan B:");
                foreach (var w in b.WaitStats.OrderByDescending(w => w.WaitTimeMs))
                    writer.WriteLine($"      - {w.WaitType} {w.WaitTimeMs:N0}ms");
            }
        }

        writer.WriteLine();
    }

    private static void WriteMetricLine(
        TextWriter writer, string label,
        double? valA, double? valB,
        string format, string unit, string betterWord,
        bool lowerIsBetter)
    {
        if (!valA.HasValue && !valB.HasValue) return;

        var aStr = valA.HasValue ? valA.Value.ToString(format) + unit : "N/A";
        var bStr = valB.HasValue ? valB.Value.ToString(format) + unit : "N/A";

        var padded = $"  {label}:".PadRight(22);

        if (!valA.HasValue || !valB.HasValue)
        {
            writer.WriteLine($"{padded}{aStr} -> {bStr}");
            return;
        }

        var a = valA.Value;
        var b = valB.Value;

        if (a == 0 && b == 0)
        {
            // Both zero — skip entirely, not useful
            return;
        }

        string delta;
        if (a == 0)
        {
            delta = "(new)";
        }
        else if (b == 0)
        {
            delta = "(eliminated)";
        }
        else
        {
            var pct = Math.Abs((b - a) / a) * 100;
            if (pct > 9999) pct = 9999;
            var pctStr = pct >= 100 ? $"{pct:N0}" : $"{pct:N1}";

            bool improved = lowerIsBetter ? b < a : b > a;
            var worseWord = betterWord switch
            {
                "cheaper" => "costlier",
                "faster" => "slower",
                "fewer" => "more",
                "less" => "more",
                _ => "worse"
            };

            delta = improved ? $"({pctStr}% {betterWord})" : $"({pctStr}% {worseWord})";
        }

        writer.WriteLine($"{padded}{aStr} -> {bStr}  {delta}");
    }

    private static void WriteCountDelta(TextWriter writer, string label, int countA, int countB)
    {
        if (countA == 0 && countB == 0) return;

        var padded = $"  {label}:".PadRight(22);

        if (countA == countB)
        {
            writer.WriteLine($"{padded}{countA} -> {countB}  (no change)");
            return;
        }

        var diff = countB - countA;
        string delta;
        if (diff < 0)
            delta = $"({Math.Abs(diff)} resolved)";
        else
            delta = $"({diff} new)";

        writer.WriteLine($"{padded}{countA} -> {countB}  {delta}");
    }

    // --- Statement matching ---

    private record StatementMatch(StatementResult? A, StatementResult? B);

    private static List<StatementMatch> MatchStatements(
        AnalysisResult planA, AnalysisResult planB)
    {
        var matches = new List<StatementMatch>();
        var usedA = new HashSet<int>();
        var usedB = new HashSet<int>();

        // Pass 1: match by QueryHash
        for (int i = 0; i < planA.Statements.Count; i++)
        {
            var a = planA.Statements[i];
            if (string.IsNullOrEmpty(a.QueryHash)) continue;

            for (int j = 0; j < planB.Statements.Count; j++)
            {
                if (usedB.Contains(j)) continue;
                if (a.QueryHash == planB.Statements[j].QueryHash)
                {
                    matches.Add(new StatementMatch(a, planB.Statements[j]));
                    usedA.Add(i);
                    usedB.Add(j);
                    break;
                }
            }
        }

        // Pass 2: positional fallback for unmatched
        int maxCount = Math.Max(planA.Statements.Count, planB.Statements.Count);
        for (int i = 0; i < maxCount; i++)
        {
            var a = i < planA.Statements.Count && !usedA.Contains(i)
                ? planA.Statements[i] : null;
            var b = i < planB.Statements.Count && !usedB.Contains(i)
                ? planB.Statements[i] : null;

            if (a != null || b != null)
                matches.Add(new StatementMatch(a, b));
        }

        return matches;
    }

    // --- Helpers ---

    private static (long logicalReads, long physicalReads) SumTreeIO(OperatorResult? root)
    {
        if (root == null) return (0, 0);

        long lr = root.ActualLogicalReads ?? 0;
        long pr = root.ActualPhysicalReads ?? 0;

        foreach (var child in root.Children)
        {
            var (clr, cpr) = SumTreeIO(child);
            lr += clr;
            pr += cpr;
        }

        return (lr, pr);
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var singleLine = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return singleLine.Length <= maxLength
            ? singleLine
            : singleLine[..maxLength] + "...";
    }
}
