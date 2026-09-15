using System.IO;

namespace PlanViewer.Core.Output;

/// <summary>
/// Compares two analysed plans, as a structure (<see cref="Build"/>) or as the text report
/// (<see cref="Compare"/>) that is rendered from it.
///
/// <para><b>The text is a contract.</b> <c>compare_plans</c> returns it to a model over MCP
/// verbatim, and people copy it out of the comparison window. Its bytes are pinned by
/// <c>ComparisonTextCharacterizationTests</c> against a recorded baseline; a change to any format
/// string, pad width or rounding rule below is a change to what those consumers see, and the
/// baseline exists so that it has to be a decision rather than a side effect.</para>
///
/// <para>Everything a reader needs is therefore computed once, into <see cref="ComparisonResult"/>,
/// and the report is a layout pass over that. The comparison window draws the same structure with
/// colour instead of parentheses, so the two views cannot disagree about the numbers.</para>
/// </summary>
public static class ComparisonFormatter
{
    /// <summary>
    /// The text comparison report.
    /// </summary>
    public static string Compare(
        AnalysisResult planA, AnalysisResult planB,
        string labelA, string labelB)
    {
        using var writer = new StringWriter();
        WriteReport(Build(planA, planB, labelA, labelB), writer);
        return writer.ToString();
    }

    /// <summary>
    /// The same comparison as a structure: per-statement metric rows carrying both sides, the size
    /// of each change and which way it went, waits paired by type, and an overall verdict when the
    /// numbers support one.
    /// </summary>
    public static ComparisonResult Build(
        AnalysisResult planA, AnalysisResult planB,
        string labelA, string labelB)
    {
        string? note = null;
        if (planA.Summary.HasActualStats != planB.Summary.HasActualStats)
        {
            var estimatedSide = planA.Summary.HasActualStats ? "Plan B" : "Plan A";
            note = $"Note: {estimatedSide} is an estimated plan. " +
                   "Runtime metrics only available for the actual plan.";
        }

        var statements = new List<ComparisonStatement>();
        var matchedPairs = new List<(StatementResult A, StatementResult B)>();

        int index = 0;
        foreach (var match in MatchStatements(planA, planB))
        {
            index++;

            if (match.A != null && match.B != null)
            {
                statements.Add(BuildStatement(match.A, match.B, index));
                matchedPairs.Add((match.A, match.B));
            }
            else if (match.A != null)
            {
                statements.Add(BuildOneSidedStatement(match.A, index, StatementPresence.OnlyInA));
            }
            else if (match.B != null)
            {
                statements.Add(BuildOneSidedStatement(match.B, index, StatementPresence.OnlyInB));
            }
        }

        return new ComparisonResult
        {
            LabelA = labelA,
            LabelB = labelB,
            EstimatedPlanNote = note,
            Statements = statements,
            Verdict = BuildVerdict(matchedPairs, statements.Count)
        };
    }

    // --- Text rendering ------------------------------------------------------------------------

    private static void WriteReport(ComparisonResult result, TextWriter writer)
    {
        writer.WriteLine("=== Plan Comparison ===");
        writer.WriteLine($"Plan A: {result.LabelA}");
        writer.WriteLine($"Plan B: {result.LabelB}");

        // Note estimated vs actual mismatch
        if (result.EstimatedPlanNote != null)
        {
            writer.WriteLine();
            writer.WriteLine(result.EstimatedPlanNote);
        }

        writer.WriteLine();

        if (result.Statements.Count == 0)
        {
            writer.WriteLine("No statements to compare.");
            return;
        }

        foreach (var statement in result.Statements)
        {
            if (statement.Presence == StatementPresence.Both)
            {
                WriteStatementComparison(statement, writer);
                continue;
            }

            var side = statement.Presence == StatementPresence.OnlyInA ? "A" : "B";
            writer.WriteLine($"--- Statement {statement.Index} (only in Plan {side}) ---");
            writer.WriteLine(statement.StatementText);
            writer.WriteLine("  (no comparison available)");
            writer.WriteLine();
        }
    }

    private static void WriteStatementComparison(ComparisonStatement statement, TextWriter writer)
    {
        writer.WriteLine($"--- Statement {statement.Index} ---");
        writer.WriteLine(statement.StatementText);
        writer.WriteLine();

        foreach (var metric in statement.Metrics)
        {
            // 22 columns puts the widest label ("Missing indexes:") two spaces clear of the values,
            // so every "A -> B" in the block starts in the same column.
            var label = $"  {metric.Label}:".PadRight(22);
            writer.WriteLine(metric.DeltaLabel == null
                ? $"{label}{metric.DisplayA} -> {metric.DisplayB}"
                : $"{label}{metric.DisplayA} -> {metric.DisplayB}  ({metric.DeltaLabel})");
        }

        if (statement.OrderedWaitsA.Count > 0 || statement.OrderedWaitsB.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("  Wait stats:");
            if (statement.OrderedWaitsA.Count > 0)
            {
                writer.WriteLine("    Plan A:");
                foreach (var wait in statement.OrderedWaitsA)
                    writer.WriteLine($"      - {wait.WaitType} {MetricFormatter.FormatDuration(wait.WaitTimeMs)}");
            }
            if (statement.OrderedWaitsA.Count > 0 && statement.OrderedWaitsB.Count > 0)
                writer.WriteLine();
            if (statement.OrderedWaitsB.Count > 0)
            {
                writer.WriteLine("    Plan B:");
                foreach (var wait in statement.OrderedWaitsB)
                    writer.WriteLine($"      - {wait.WaitType} {MetricFormatter.FormatDuration(wait.WaitTimeMs)}");
            }
        }

        writer.WriteLine();
    }

    // --- Statement building --------------------------------------------------------------------

    private static ComparisonStatement BuildOneSidedStatement(
        StatementResult statement, int index, StatementPresence presence) =>
        new()
        {
            Index = index,
            StatementText = TruncateText(statement.StatementText, 500),
            Presence = presence
        };

    private static ComparisonStatement BuildStatement(StatementResult a, StatementResult b, int index)
    {
        var metrics = new List<ComparisonMetric>();

        // Estimated metrics (always available)
        AddMetric(metrics, "Estimated cost", a.EstimatedCost, b.EstimatedCost,
            FormatCost, "cheaper", "costlier", ComparisonPolarity.LowerIsBetter);

        /* Rows carry no verdict. The report has always said "fewer"/"more" and still does, but a
           plan estimating fewer rows is not a plan doing less work — very often it is the estimate
           getting worse, which is the opposite of an improvement. Size shown, colour withheld. */
        AddMetric(metrics, "Estimated rows", a.EstimatedRows, b.EstimatedRows,
            FormatCount, "fewer", "more", ComparisonPolarity.None);

        // Runtime (actual plans only)
        if (a.QueryTime != null || b.QueryTime != null)
        {
            AddMetric(metrics, "Runtime", a.QueryTime?.ElapsedTimeMs, b.QueryTime?.ElapsedTimeMs,
                FormatDuration, "faster", "slower", ComparisonPolarity.LowerIsBetter);
            AddMetric(metrics, "CPU time", a.QueryTime?.CpuTimeMs, b.QueryTime?.CpuTimeMs,
                FormatDuration, "faster", "slower", ComparisonPolarity.LowerIsBetter);
        }

        // I/O from operator tree
        var (aLogical, aPhysical) = SumTreeIO(a.OperatorTree);
        var (bLogical, bPhysical) = SumTreeIO(b.OperatorTree);
        if (aLogical > 0 || bLogical > 0)
            AddMetric(metrics, "Logical reads", aLogical, bLogical,
                FormatCount, "fewer", "more", ComparisonPolarity.LowerIsBetter);
        if (aPhysical > 0 || bPhysical > 0)
            AddMetric(metrics, "Physical reads", aPhysical, bPhysical,
                FormatCount, "fewer", "more", ComparisonPolarity.LowerIsBetter);

        // Memory grant
        if ((a.MemoryGrant != null && a.MemoryGrant.GrantedKB > 0) ||
            (b.MemoryGrant != null && b.MemoryGrant.GrantedKB > 0))
        {
            var aGrantMB = a.MemoryGrant != null ? a.MemoryGrant.GrantedKB / 1024.0 : 0;
            var bGrantMB = b.MemoryGrant != null ? b.MemoryGrant.GrantedKB / 1024.0 : 0;
            // Fixed at MB on both sides: a per-side scale would put "512 KB" opposite "2.1 GB"
            // and make the one line whose whole job is a side-by-side unreadable.
            AddMetric(metrics, "Memory grant", aGrantMB, bGrantMB,
                FormatMegabytes, "less", "more", ComparisonPolarity.LowerIsBetter);
        }

        // DOP — show raw values, no percentage
        if (a.DegreeOfParallelism > 0 || b.DegreeOfParallelism > 0)
        {
            metrics.Add(new ComparisonMetric
            {
                Label = "DOP",
                ValueA = a.DegreeOfParallelism,
                ValueB = b.DegreeOfParallelism,
                DisplayA = a.DegreeOfParallelism.ToString(),
                DisplayB = b.DegreeOfParallelism.ToString(),
                Direction = ComparisonDirection.Neutral,
                Polarity = ComparisonPolarity.None
            });
        }

        // Warning and missing index counts
        AddCount(metrics, "Warnings", a.Warnings.Count, b.Warnings.Count);
        AddCount(metrics, "Missing indexes", a.MissingIndexes.Count, b.MissingIndexes.Count);

        var orderedWaitsA = a.WaitStats.OrderByDescending(w => w.WaitTimeMs).ToList();
        var orderedWaitsB = b.WaitStats.OrderByDescending(w => w.WaitTimeMs).ToList();

        var (netDirection, netBasis) = NetOf(a, b);

        return new ComparisonStatement
        {
            Index = index,
            StatementText = TruncateText(a.StatementText, 500),
            Presence = StatementPresence.Both,
            Metrics = metrics,
            OrderedWaitsA = orderedWaitsA,
            OrderedWaitsB = orderedWaitsB,
            Waits = MergeWaits(orderedWaitsA, orderedWaitsB),
            NetDirection = netDirection,
            NetBasis = netBasis
        };
    }

    /// <summary>
    /// Whether the statement as a whole got better or worse. Elapsed time when both plans measured
    /// one, because that is the only number here that was observed rather than predicted; the
    /// optimizer's cost otherwise, named so nobody mistakes the one for the other.
    /// </summary>
    private static (ComparisonDirection Direction, string Basis) NetOf(StatementResult a, StatementResult b)
    {
        if (a.QueryTime != null && b.QueryTime != null)
        {
            return (
                DirectionOf(a.QueryTime.ElapsedTimeMs, b.QueryTime.ElapsedTimeMs, ComparisonPolarity.LowerIsBetter),
                "elapsed time");
        }

        return (DirectionOf(a.EstimatedCost, b.EstimatedCost, ComparisonPolarity.LowerIsBetter), "estimated cost");
    }

    // --- Metric rows ---------------------------------------------------------------------------

    /// <param name="lowerWord">The word the report prints when B is the smaller of the two —
    /// "cheaper", "faster", "fewer", "less".</param>
    /// <param name="higherWord">And when it is not. Note that equal values take this branch, which
    /// is why comparing a plan against itself reads "(0.0% costlier)". That wording is frozen by
    /// the text baseline; <see cref="ComparisonMetric.Direction"/> calls it Neutral, so the
    /// window's chip does not repeat the mistake.</param>
    private static void AddMetric(
        List<ComparisonMetric> metrics,
        string label,
        double? valueA, double? valueB,
        Func<double, string> formatValue,
        string lowerWord, string higherWord,
        ComparisonPolarity polarity)
    {
        if (!valueA.HasValue && !valueB.HasValue) return;

        var displayA = valueA.HasValue ? formatValue(valueA.Value) : "N/A";
        var displayB = valueB.HasValue ? formatValue(valueB.Value) : "N/A";

        if (!valueA.HasValue || !valueB.HasValue)
        {
            // One side does not have this metric at all. Both values are shown; no change is
            // claimed, because there is nothing to have changed from.
            metrics.Add(new ComparisonMetric
            {
                Label = label,
                ValueA = valueA,
                ValueB = valueB,
                DisplayA = displayA,
                DisplayB = displayB,
                Direction = ComparisonDirection.Neutral,
                Polarity = polarity
            });
            return;
        }

        var a = valueA.Value;
        var b = valueB.Value;

        if (a == 0 && b == 0)
        {
            // Both zero — skip entirely, not useful
            return;
        }

        string deltaLabel;
        double? deltaPercent = null;

        if (a == 0)
        {
            deltaLabel = "new";
        }
        else if (b == 0)
        {
            deltaLabel = "eliminated";
        }
        else
        {
            var pct = Math.Abs((b - a) / a) * 100;
            if (pct > 9999) pct = 9999;
            deltaPercent = pct;
            deltaLabel = $"{FormatPercent(pct)}% {(b < a ? lowerWord : higherWord)}";
        }

        metrics.Add(new ComparisonMetric
        {
            Label = label,
            ValueA = a,
            ValueB = b,
            DisplayA = displayA,
            DisplayB = displayB,
            DeltaLabel = deltaLabel,
            DeltaPercent = deltaPercent,
            Direction = DirectionOf(a, b, polarity),
            Polarity = polarity
        });
    }

    /// <summary>
    /// A count of findings. No percentage: one warning becoming three is not usefully "200% more",
    /// and a chip saying so would be the loudest thing on a card that has real percentages on it.
    /// </summary>
    private static void AddCount(List<ComparisonMetric> metrics, string label, int countA, int countB)
    {
        if (countA == 0 && countB == 0) return;

        var difference = countB - countA;
        var deltaLabel =
            difference == 0 ? "no change"
            : difference < 0 ? $"{Math.Abs(difference)} resolved"
            : $"{difference} new";

        metrics.Add(new ComparisonMetric
        {
            Label = label,
            ValueA = countA,
            ValueB = countB,
            DisplayA = countA.ToString(),
            DisplayB = countB.ToString(),
            DeltaLabel = deltaLabel,
            Direction = DirectionOf(countA, countB, ComparisonPolarity.LowerIsBetter),
            Polarity = ComparisonPolarity.LowerIsBetter
        });
    }

    /// <summary>
    /// The one place better/worse is decided, and it decides from the metric's declared polarity
    /// rather than from the sign of the change. Equal values and polarity-free metrics are Neutral.
    /// </summary>
    private static ComparisonDirection DirectionOf(double a, double b, ComparisonPolarity polarity)
    {
        if (polarity == ComparisonPolarity.None || a == b)
            return ComparisonDirection.Neutral;

        var fell = b < a;
        return polarity == ComparisonPolarity.LowerIsBetter
            ? (fell ? ComparisonDirection.Better : ComparisonDirection.Worse)
            : (fell ? ComparisonDirection.Worse : ComparisonDirection.Better);
    }

    // --- Waits ---------------------------------------------------------------------------------

    /// <summary>
    /// Pairs the two plans' waits by type for a side-by-side view. Plan A's waits keep their order,
    /// longest first; types only Plan B waited on follow, in theirs.
    /// </summary>
    private static List<ComparisonWait> MergeWaits(
        IReadOnlyList<WaitStatResult> orderedA, IReadOnlyList<WaitStatResult> orderedB)
    {
        var totalsA = TotalsByType(orderedA);
        var totalsB = TotalsByType(orderedB);

        var waits = new List<ComparisonWait>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var wait in orderedA.Concat(orderedB))
        {
            if (seen.Add(wait.WaitType))
                waits.Add(BuildWait(wait.WaitType, totalsA, totalsB));
        }

        return waits;
    }

    private static Dictionary<string, long> TotalsByType(IReadOnlyList<WaitStatResult> waits) =>
        waits
            .GroupBy(wait => wait.WaitType, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(wait => wait.WaitTimeMs), StringComparer.Ordinal);

    private static ComparisonWait BuildWait(
        string waitType, Dictionary<string, long> totalsA, Dictionary<string, long> totalsB)
    {
        var hasA = totalsA.TryGetValue(waitType, out var msA);
        var hasB = totalsB.TryGetValue(waitType, out var msB);

        string? deltaLabel = null;
        double? deltaPercent = null;
        var direction = ComparisonDirection.Neutral;

        if (hasA && hasB)
        {
            if (msA == msB)
            {
                deltaLabel = "no change";
                deltaPercent = 0;
            }
            else if (msA == 0)
            {
                deltaLabel = "new";
                direction = ComparisonDirection.Worse;
            }
            else if (msB == 0)
            {
                deltaLabel = "eliminated";
                direction = ComparisonDirection.Better;
            }
            else
            {
                var pct = Math.Abs((double)(msB - msA) / msA) * 100;
                if (pct > 9999) pct = 9999;
                deltaPercent = pct;
                deltaLabel = $"{FormatPercent(pct)}% {(msB < msA ? "less" : "more")}";
                direction = msB < msA ? ComparisonDirection.Better : ComparisonDirection.Worse;
            }
        }
        else if (hasA)
        {
            deltaLabel = "eliminated";
            direction = ComparisonDirection.Better;
        }
        else
        {
            deltaLabel = "new";
            direction = ComparisonDirection.Worse;
        }

        return new ComparisonWait
        {
            WaitType = waitType,
            WaitTimeMsA = hasA ? msA : null,
            WaitTimeMsB = hasB ? msB : null,
            DisplayA = hasA ? MetricFormatter.FormatDuration(msA) : AbsentValue,
            DisplayB = hasB ? MetricFormatter.FormatDuration(msB) : AbsentValue,
            DeltaLabel = deltaLabel,
            DeltaPercent = deltaPercent,
            Direction = direction
        };
    }

    // --- Verdict -------------------------------------------------------------------------------

    /// <summary>
    /// The headline, when the numbers honestly support one.
    ///
    /// <para>Elapsed time is only used when every matched statement was timed on both sides, which
    /// rules out comparing an actual plan against an estimated one — that pairing has no runtime to
    /// compare and would otherwise produce a confident sentence out of half a measurement.
    /// Estimated cost is the fallback and says so. Neither available, or Plan A summing to zero so
    /// there is nothing to be a percentage of, and there is no verdict at all.</para>
    /// </summary>
    private static ComparisonVerdict? BuildVerdict(
        IReadOnlyList<(StatementResult A, StatementResult B)> matchedPairs, int totalStatements)
    {
        if (matchedPairs.Count == 0) return null;

        double elapsedA = 0, elapsedB = 0, costA = 0, costB = 0;
        var everyPairTimed = true;

        foreach (var (a, b) in matchedPairs)
        {
            if (a.QueryTime != null && b.QueryTime != null)
            {
                elapsedA += a.QueryTime.ElapsedTimeMs;
                elapsedB += b.QueryTime.ElapsedTimeMs;
            }
            else
            {
                everyPairTimed = false;
            }

            costA += a.EstimatedCost;
            costB += b.EstimatedCost;
        }

        string basis, betterWord, worseWord;
        double valueA, valueB;

        if (everyPairTimed && elapsedA > 0)
        {
            (basis, betterWord, worseWord) = ("elapsed time", "faster", "slower");
            (valueA, valueB) = (elapsedA, elapsedB);
        }
        else if (costA > 0)
        {
            (basis, betterWord, worseWord) = ("estimated cost", "cheaper", "costlier");
            (valueA, valueB) = (costA, costB);
        }
        else
        {
            return null;
        }

        /* "overall" is only true when nothing was left out of the sum. When the plans do not hold
           the same statements, the sentence names what it actually covered. */
        var scope = matchedPairs.Count == totalStatements
            ? "overall"
            : $"across the {matchedPairs.Count} statement{(matchedPairs.Count == 1 ? "" : "s")} both plans share";

        var qualifier = basis == "estimated cost" ? " (estimated cost)" : "";

        var pct = Math.Abs((valueB - valueA) / valueA) * 100;
        if (pct > 9999) pct = 9999;

        // Below this the difference disappears into the one decimal place the percentage is
        // printed to, and "Plan B is 0.0% faster" is a worse answer than saying they are the same.
        if (pct < 0.05)
        {
            return new ComparisonVerdict
            {
                Text = $"Plan A and Plan B are effectively the same {scope}{qualifier}",
                Direction = ComparisonDirection.Neutral,
                Basis = basis
            };
        }

        var improved = valueB < valueA;
        return new ComparisonVerdict
        {
            Text = $"Plan B is {FormatPercent(pct)}% {(improved ? betterWord : worseWord)} {scope}{qualifier}",
            Direction = improved ? ComparisonDirection.Better : ComparisonDirection.Worse,
            Basis = basis
        };
    }

    // --- Statement matching --------------------------------------------------------------------

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

    // --- Helpers -------------------------------------------------------------------------------

    /// <summary>What a side shows when it has no value at all. Text-report rows say "N/A"; this is
    /// for the paired wait rows, which the report does not print.</summary>
    private const string AbsentValue = "—";

    // The per-metric display shapes. Costs and durations go through the shared
    // MetricFormatter so a number reads the same here as it does in the properties
    // panel; counts and megabytes are local because nothing else displays them.
    private static string FormatCost(double cost) => MetricFormatter.FormatCost(cost);
    private static string FormatDuration(double ms) => MetricFormatter.FormatDuration((long)ms);
    private static string FormatCount(double count) => count.ToString("N0");
    private static string FormatMegabytes(double mb) => mb.ToString("N1") + " MB";

    /// <summary>
    /// Percentages keep one decimal until they reach three digits, where the decimal is noise —
    /// "1,386% slower" rather than "1,386.4% slower".
    /// </summary>
    private static string FormatPercent(double pct) => pct >= 100 ? $"{pct:N0}" : $"{pct:N1}";

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
