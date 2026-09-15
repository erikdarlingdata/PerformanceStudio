namespace PlanViewer.Core.Output;

/// <summary>
/// Which way a metric moved between the two plans, decided per metric rather than read off the
/// sign of the change.
///
/// <para>The sign alone cannot answer this. A duration falling is better; a duration rising is
/// worse; an estimated row count doing either is neither, because the optimizer estimating fewer
/// rows is not the query doing less work — very often it is the estimate getting worse. Metrics
/// declare their own polarity (<see cref="ComparisonPolarity"/>) and this is derived from it.</para>
/// </summary>
public enum ComparisonDirection
{
    /// <summary>No verdict: the values are equal, one side is missing, or the metric is one where
    /// moving in either direction means nothing on its own.</summary>
    Neutral,
    Better,
    Worse
}

/// <summary>
/// Which way a given metric has to move to count as an improvement. Stated explicitly at each
/// metric's construction site, never inferred.
/// </summary>
public enum ComparisonPolarity
{
    /// <summary>Durations, costs, reads, grants, warning counts.</summary>
    LowerIsBetter,

    /// <summary>Nothing uses this today. It is here so the day something does — a cache hit rate,
    /// a batch-mode operator count — the author has somewhere to say so rather than a reason to
    /// reach for a sign test.</summary>
    HigherIsBetter,

    /// <summary>Estimated rows and DOP: real signal, no verdict. Shown with its size and no
    /// better/worse colour.</summary>
    None
}

/// <summary>Whether a compared statement was found in one plan or both.</summary>
public enum StatementPresence
{
    Both,
    OnlyInA,
    OnlyInB
}

/// <summary>
/// One metric line of a statement comparison: the label, both sides, and what changed.
///
/// <para><see cref="DisplayA"/>, <see cref="DisplayB"/> and <see cref="DeltaLabel"/> are the exact
/// strings the text report prints, so the report can be rendered from this without a second
/// formatting path that could drift from the first. <see cref="ValueA"/>, <see cref="ValueB"/> and
/// <see cref="DeltaPercent"/> are the raw numbers, for a caller that wants to sort or colour.</para>
/// </summary>
public sealed class ComparisonMetric
{
    public required string Label { get; init; }

    /// <summary>Null when the metric does not exist on that side at all — an estimated plan has no
    /// runtime. Distinct from zero, which is a measurement.</summary>
    public double? ValueA { get; init; }

    public double? ValueB { get; init; }

    /// <summary>Formatted for a human, or "N/A" when <see cref="ValueA"/> is null.</summary>
    public required string DisplayA { get; init; }

    public required string DisplayB { get; init; }

    /// <summary>
    /// The change, without the parentheses the text report wraps it in: "99.3% cheaper", "new",
    /// "eliminated", "no change", "3 resolved". Null when no change is printed at all, which is
    /// the case for DOP and for any metric present on only one side.
    /// </summary>
    public string? DeltaLabel { get; init; }

    /// <summary>
    /// Size of the change as a percentage of Plan A, always positive, capped at 9999 the way the
    /// report caps it. Null when a percentage would be meaningless or misleading: a side is
    /// missing, a side is zero, or the metric is a count where "1 -&gt; 3" is not usefully 200%.
    /// </summary>
    public double? DeltaPercent { get; init; }

    public ComparisonDirection Direction { get; init; }

    public ComparisonPolarity Polarity { get; init; }
}

/// <summary>
/// One wait type, paired across the two plans. Waits the plans do not share still get a row, with
/// the absent side null.
/// </summary>
public sealed class ComparisonWait
{
    public required string WaitType { get; init; }

    public long? WaitTimeMsA { get; init; }

    public long? WaitTimeMsB { get; init; }

    public required string DisplayA { get; init; }

    public required string DisplayB { get; init; }

    public string? DeltaLabel { get; init; }

    public double? DeltaPercent { get; init; }

    public ComparisonDirection Direction { get; init; }
}

/// <summary>
/// One statement of the comparison. Statements arrive in the order the report prints them, and so
/// do <see cref="Metrics"/>; a caller that wants the worst regression at the top sorts a copy with
/// <see cref="ComparisonOrdering.WorstFirst"/> rather than getting it pre-sorted, because the text
/// report's order is part of a recorded contract.
/// </summary>
public sealed class ComparisonStatement
{
    /// <summary>1-based, exactly as the report numbers it.</summary>
    public required int Index { get; init; }

    /// <summary>Flattened to one line and truncated, as printed.</summary>
    public required string StatementText { get; init; }

    public required StatementPresence Presence { get; init; }

    public IReadOnlyList<ComparisonMetric> Metrics { get; init; } = [];

    /// <summary>Plan A's waits, longest first — the order the text report lists them in.</summary>
    public IReadOnlyList<WaitStatResult> OrderedWaitsA { get; init; } = [];

    public IReadOnlyList<WaitStatResult> OrderedWaitsB { get; init; } = [];

    /// <summary>The same waits paired by type, for a side-by-side view.</summary>
    public IReadOnlyList<ComparisonWait> Waits { get; init; } = [];

    /// <summary>
    /// Whether this statement as a whole got better or worse, on <see cref="NetBasis"/>. Elapsed
    /// time decides it when both plans measured one; estimated cost decides it otherwise, which is
    /// the optimizer's opinion rather than a measurement and is labelled as such.
    /// </summary>
    public ComparisonDirection NetDirection { get; init; }

    /// <summary>"elapsed time" or "estimated cost"; null for a statement present in one plan only.</summary>
    public string? NetBasis { get; init; }
}

/// <summary>
/// The one-line answer to "which plan is better", when there is an honest one. Null on the result
/// when there is not — two plans with no runtime and no cost between them get no verdict rather
/// than a manufactured one.
/// </summary>
public sealed class ComparisonVerdict
{
    public required string Text { get; init; }

    public required ComparisonDirection Direction { get; init; }

    /// <summary>"elapsed time" or "estimated cost".</summary>
    public required string Basis { get; init; }
}

/// <summary>
/// A whole plan comparison, structured. <see cref="ComparisonFormatter.Compare"/> renders its text
/// report from one of these, and the comparison window draws its diff from the same one, so the two
/// can only ever disagree about presentation.
/// </summary>
public sealed class ComparisonResult
{
    public required string LabelA { get; init; }

    public required string LabelB { get; init; }

    /// <summary>The "one of these is an estimated plan" line, or null when both plans are the same
    /// kind. Carried whole because the text report prints it verbatim.</summary>
    public string? EstimatedPlanNote { get; init; }

    public IReadOnlyList<ComparisonStatement> Statements { get; init; } = [];

    public ComparisonVerdict? Verdict { get; init; }
}

/// <summary>
/// How a reader wants the metrics ordered, as opposed to how the report prints them.
/// </summary>
public static class ComparisonOrdering
{
    /// <summary>
    /// Regressions first, biggest first, then the metrics with no verdict in their original order,
    /// then the improvements, biggest first.
    ///
    /// <para>A regression with no percentage — "2 new" warnings, where a percentage of a count
    /// would be noise — sorts below the measured regressions rather than above them, because a
    /// number that exists should outrank one that does not.</para>
    /// </summary>
    public static IReadOnlyList<ComparisonMetric> WorstFirst(IEnumerable<ComparisonMetric> metrics) =>
        metrics
            .OrderBy(metric => metric.Direction switch
            {
                ComparisonDirection.Worse => 0,
                ComparisonDirection.Neutral => 1,
                _ => 2
            })
            /* Neutral rows are all pinned to one key so LINQ's stable sort leaves them in the order
               the report printed them. Sorting them by size would shuffle DOP against estimated
               rows for no reason a reader could name. */
            .ThenByDescending(metric =>
                metric.Direction == ComparisonDirection.Neutral ? 0d : metric.DeltaPercent ?? -1d)
            .ToList();
}
