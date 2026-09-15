using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using PlanViewer.Core.Output;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The structure behind the comparison: what each metric row says changed, and — the part that
/// cannot be read off a sign — which way that counts as having gone.
///
/// <para><b>Why direction is tested this hard.</b> The obvious implementation is
/// <c>b &lt; a ? better : worse</c>, and it is wrong twice over. It calls two identical plans a
/// regression, because equality falls through to the worse branch — which is exactly what the text
/// report has always printed, and exactly what must NOT reach a red chip. And it colours estimated
/// rows, where falling is not an improvement at all: the optimizer estimating fewer rows is very
/// often the estimate getting worse. Every metric states its own polarity and these pin it.</para>
///
/// <para>Plans are built by hand rather than loaded from fixtures. The arithmetic under test wants
/// a 100ms statement against a 50ms one and a warning count of exactly three; hunting for a fixture
/// pair that happens to produce those would test the fixtures.</para>
/// </summary>
public class ComparisonModelTests
{
    // --- Delta arithmetic ----------------------------------------------------------------------

    [Fact]
    public void AHalvedRuntimeIsFiftyPercentFasterAndCountsAsBetter()
    {
        var metric = Metric(
            Compare(Plan(Statement(elapsedMs: 100)), Plan(Statement(elapsedMs: 50))),
            "Runtime");

        Assert.Equal(100d, metric.ValueA);
        Assert.Equal(50d, metric.ValueB);
        Assert.Equal(50d, metric.DeltaPercent);
        Assert.Equal("50.0% faster", metric.DeltaLabel);
        Assert.Equal(ComparisonDirection.Better, metric.Direction);
    }

    [Fact]
    public void AMultipliedRuntimeIsAPercentageOfPlanANotOfPlanB()
    {
        /* 100 -> 1,486 is 1,386% more than A, and 93% less than B. The report has always spoken in
           terms of A, which is what makes "1,386% slower" mean "took fourteen times as long". */
        var metric = Metric(
            Compare(Plan(Statement(elapsedMs: 100)), Plan(Statement(elapsedMs: 1486))),
            "Runtime");

        Assert.Equal(1386d, metric.DeltaPercent);
        Assert.Equal("1,386% slower", metric.DeltaLabel);
        Assert.Equal(ComparisonDirection.Worse, metric.Direction);
    }

    [Fact]
    public void ARunawayPercentageIsCappedRatherThanPrinted()
    {
        // 1ms -> 10 minutes is 59,999,900%. A chip that wide is not a fact, it is a layout bug.
        var metric = Metric(
            Compare(Plan(Statement(elapsedMs: 1)), Plan(Statement(elapsedMs: 600_000))),
            "Runtime");

        Assert.Equal(9999d, metric.DeltaPercent);
        Assert.Equal("9,999% slower", metric.DeltaLabel);
    }

    [Fact]
    public void PercentagesKeepADecimalUntilTheyReachThreeDigits()
    {
        Assert.Equal("99.3% faster", Metric(
            Compare(Plan(Statement(elapsedMs: 1000)), Plan(Statement(elapsedMs: 7))), "Runtime").DeltaLabel);

        Assert.Equal("100% slower", Metric(
            Compare(Plan(Statement(elapsedMs: 50)), Plan(Statement(elapsedMs: 100))), "Runtime").DeltaLabel);
    }

    // --- Direction is per metric, never inferred from the sign ---------------------------------

    [Fact]
    public void TwoIdenticalPlansAreNeutral_EvenThoughTheReportSaysCostlier()
    {
        /* The frozen text reads "(0.0% costlier)" because equality falls through to the worse
           branch of a two-way choice. That wording is a contract and stays; painting it red is not,
           and this is the assertion that stops the window inheriting the bug. */
        var statement = Compare(Plan(Statement(cost: 12.5, elapsedMs: 80)), Plan(Statement(cost: 12.5, elapsedMs: 80)))
            .Statements.Single();

        Assert.Equal("0.0% costlier", statement.Metrics.Single(m => m.Label == "Estimated cost").DeltaLabel);
        Assert.All(statement.Metrics, metric =>
            Assert.Equal(ComparisonDirection.Neutral, metric.Direction));
        Assert.Equal(ComparisonDirection.Neutral, statement.NetDirection);
    }

    [Theory]
    [InlineData(10d, 1000d)]
    [InlineData(1000d, 10d)]
    public void EstimatedRowsMovingEitherWayIsNeverAVerdict(double rowsA, double rowsB)
    {
        /* The trap the whole polarity enum exists for. Fewer estimated rows is not less work — it
           is frequently a worse estimate — so the size of the change is reported and the judgement
           is withheld. */
        var metric = Metric(Compare(Plan(Statement(rows: rowsA)), Plan(Statement(rows: rowsB))), "Estimated rows");

        Assert.Equal(ComparisonPolarity.None, metric.Polarity);
        Assert.Equal(ComparisonDirection.Neutral, metric.Direction);
        Assert.NotNull(metric.DeltaPercent);
    }

    [Fact]
    public void DopIsShownRawWithNoDeltaAndNoVerdict()
    {
        var metric = Metric(Compare(Plan(Statement(dop: 1)), Plan(Statement(dop: 8))), "DOP");

        Assert.Equal("1", metric.DisplayA);
        Assert.Equal("8", metric.DisplayB);
        Assert.Null(metric.DeltaLabel);
        Assert.Null(metric.DeltaPercent);
        Assert.Equal(ComparisonDirection.Neutral, metric.Direction);
    }

    [Fact]
    public void FewerWarningsIsBetterAndMoreIsWorse_WithoutInventingAPercentage()
    {
        var resolved = Metric(Compare(Plan(Statement(warnings: 3)), Plan(Statement(warnings: 1))), "Warnings");
        Assert.Equal("2 resolved", resolved.DeltaLabel);
        Assert.Equal(ComparisonDirection.Better, resolved.Direction);
        Assert.Null(resolved.DeltaPercent);

        var added = Metric(Compare(Plan(Statement(warnings: 1)), Plan(Statement(warnings: 3))), "Warnings");
        Assert.Equal("2 new", added.DeltaLabel);
        Assert.Equal(ComparisonDirection.Worse, added.Direction);

        var same = Metric(Compare(Plan(Statement(warnings: 2)), Plan(Statement(warnings: 2))), "Warnings");
        Assert.Equal("no change", same.DeltaLabel);
        Assert.Equal(ComparisonDirection.Neutral, same.Direction);
    }

    [Fact]
    public void AMetricArrivingFromNothingIsWorseAndOneGoingToNothingIsBetter()
    {
        var appeared = Metric(Compare(Plan(Statement()), Plan(Statement(grantKb: 4096))), "Memory grant");
        Assert.Equal("new", appeared.DeltaLabel);
        Assert.Null(appeared.DeltaPercent);
        Assert.Equal(ComparisonDirection.Worse, appeared.Direction);

        var gone = Metric(Compare(Plan(Statement(grantKb: 4096)), Plan(Statement())), "Memory grant");
        Assert.Equal("eliminated", gone.DeltaLabel);
        Assert.Equal(ComparisonDirection.Better, gone.Direction);
    }

    [Fact]
    public void AMetricOnlyOnePlanHasClaimsNothingAtAll()
    {
        // An actual plan against an estimated one. There is no runtime on the other side to have
        // changed from, so the row shows both cells and withholds everything else.
        var result = Compare(Plan(Statement(elapsedMs: 900)), Plan(Statement()));
        var metric = Metric(result, "Runtime");

        Assert.Equal("900ms", metric.DisplayA);
        Assert.Equal("N/A", metric.DisplayB);
        Assert.Null(metric.DeltaLabel);
        Assert.Null(metric.DeltaPercent);
        Assert.Equal(ComparisonDirection.Neutral, metric.Direction);

        Assert.Equal(
            "Note: Plan B is an estimated plan. Runtime metrics only available for the actual plan.",
            result.EstimatedPlanNote);
    }

    [Fact]
    public void AMetricThatIsZeroInBothPlansIsNotARow()
    {
        var statement = Compare(Plan(Statement(cost: 0, rows: 0)), Plan(Statement(cost: 0, rows: 0)))
            .Statements.Single();

        Assert.DoesNotContain(statement.Metrics, metric => metric.Label == "Estimated cost");
        Assert.DoesNotContain(statement.Metrics, metric => metric.Label == "Estimated rows");
        Assert.Empty(statement.Metrics);
    }

    [Fact]
    public void ReadsComeFromTheWholeOperatorTreeNotJustItsRoot()
    {
        var result = Compare(
            Plan(Statement(io: (Logical: 400, Physical: 10))),
            Plan(Statement(io: (Logical: 100, Physical: 10))));

        var logical = Metric(result, "Logical reads");
        Assert.Equal("400", logical.DisplayA);
        Assert.Equal("100", logical.DisplayB);
        Assert.Equal(ComparisonDirection.Better, logical.Direction);

        // Equal on both sides, so it is a row that exists and says nothing happened.
        Assert.Equal(ComparisonDirection.Neutral, Metric(result, "Physical reads").Direction);
    }

    // --- Waits ---------------------------------------------------------------------------------

    [Fact]
    public void WaitsArePairedByTypeAndTheUnsharedOnesStillGetARow()
    {
        var statement = Compare(
            Plan(Statement(waits: [("CXPACKET", 400), ("SOS_SCHEDULER_YIELD", 20)])),
            Plan(Statement(waits: [("CXPACKET", 100), ("PAGEIOLATCH_SH", 70)])))
            .Statements.Single();

        var shared = statement.Waits.Single(w => w.WaitType == "CXPACKET");
        Assert.Equal(400, shared.WaitTimeMsA);
        Assert.Equal(100, shared.WaitTimeMsB);
        Assert.Equal("75.0% less", shared.DeltaLabel);
        Assert.Equal(ComparisonDirection.Better, shared.Direction);

        var onlyA = statement.Waits.Single(w => w.WaitType == "SOS_SCHEDULER_YIELD");
        Assert.Equal("eliminated", onlyA.DeltaLabel);
        Assert.Null(onlyA.WaitTimeMsB);
        Assert.Equal(ComparisonDirection.Better, onlyA.Direction);

        var onlyB = statement.Waits.Single(w => w.WaitType == "PAGEIOLATCH_SH");
        Assert.Equal("new", onlyB.DeltaLabel);
        Assert.Null(onlyB.WaitTimeMsA);
        Assert.Equal(ComparisonDirection.Worse, onlyB.Direction);

        // Plan A's waits first, longest first, then the types only Plan B waited on.
        Assert.Equal(
            ["CXPACKET", "SOS_SCHEDULER_YIELD", "PAGEIOLATCH_SH"],
            statement.Waits.Select(w => w.WaitType));
    }

    [Fact]
    public void TheOrderedWaitListsAreWhatTheTextReportPrints()
    {
        var statement = Compare(
            Plan(Statement(waits: [("LATCH_EX", 5), ("CXPACKET", 400)])),
            Plan(Statement()))
            .Statements.Single();

        Assert.Equal(["CXPACKET", "LATCH_EX"], statement.OrderedWaitsA.Select(w => w.WaitType));
        Assert.Empty(statement.OrderedWaitsB);
    }

    // --- Statement-level verdict ---------------------------------------------------------------

    [Fact]
    public void AStatementIsJudgedOnElapsedTimeWhenBothPlansMeasuredOne()
    {
        // Cost says B is worse, the clock says B is better. The clock wins, because it happened.
        var statement = Compare(
            Plan(Statement(cost: 1, elapsedMs: 1000)),
            Plan(Statement(cost: 900, elapsedMs: 10)))
            .Statements.Single();

        Assert.Equal("elapsed time", statement.NetBasis);
        Assert.Equal(ComparisonDirection.Better, statement.NetDirection);
    }

    [Fact]
    public void AStatementFallsBackToCostAndSaysSo()
    {
        var statement = Compare(Plan(Statement(cost: 900)), Plan(Statement(cost: 1))).Statements.Single();

        Assert.Equal("estimated cost", statement.NetBasis);
        Assert.Equal(ComparisonDirection.Better, statement.NetDirection);
    }

    [Fact]
    public void AStatementInOnlyOnePlanIsCarriedWithoutAComparison()
    {
        var result = Compare(
            Plan(Statement(hash: "0xAA"), Statement(hash: "0xBB", text: "select 2;")),
            Plan(Statement(hash: "0xAA")));

        Assert.Equal(2, result.Statements.Count);
        Assert.Equal(StatementPresence.Both, result.Statements[0].Presence);

        var orphan = result.Statements[1];
        Assert.Equal(StatementPresence.OnlyInA, orphan.Presence);
        Assert.Equal("select 2;", orphan.StatementText);
        Assert.Empty(orphan.Metrics);
        Assert.Null(orphan.NetBasis);
        Assert.Equal(ComparisonDirection.Neutral, orphan.NetDirection);
    }

    // --- Overall verdict -----------------------------------------------------------------------

    [Fact]
    public void TheVerdictUsesRuntimeWhenEveryMatchedStatementWasTimedOnBothSides()
    {
        var verdict = Compare(
            Plan(Statement(hash: "0xAA", elapsedMs: 700), Statement(hash: "0xBB", elapsedMs: 300)),
            Plan(Statement(hash: "0xAA", elapsedMs: 400), Statement(hash: "0xBB", elapsedMs: 170)))
            .Verdict;

        Assert.NotNull(verdict);
        Assert.Equal("elapsed time", verdict.Basis);
        Assert.Equal("Plan B is 43.0% faster overall", verdict.Text);
        Assert.Equal(ComparisonDirection.Better, verdict.Direction);
    }

    [Fact]
    public void TheVerdictFallsBackToCostAndLabelsItAsAnEstimate()
    {
        var verdict = Compare(Plan(Statement(cost: 100)), Plan(Statement(cost: 143))).Verdict;

        Assert.NotNull(verdict);
        Assert.Equal("estimated cost", verdict.Basis);
        Assert.Equal("Plan B is 43.0% costlier overall (estimated cost)", verdict.Text);
        Assert.Equal(ComparisonDirection.Worse, verdict.Direction);
    }

    [Fact]
    public void AnActualPlanAgainstAnEstimatedOneNeverGetsARuntimeVerdict()
    {
        /* Half a measurement is not a measurement. Plan A ran for ten seconds and Plan B was never
           executed; a "Plan B is 100% faster" here would be a sentence invented out of a null. */
        var verdict = Compare(Plan(Statement(cost: 100, elapsedMs: 10_000)), Plan(Statement(cost: 50))).Verdict;

        Assert.NotNull(verdict);
        Assert.Equal("estimated cost", verdict.Basis);
        Assert.Contains("(estimated cost)", verdict.Text);
    }

    [Fact]
    public void AVerdictThatWouldRoundToZeroSaysTheyAreTheSameInstead()
    {
        var verdict = Compare(Plan(Statement(elapsedMs: 100_000)), Plan(Statement(elapsedMs: 100_020))).Verdict;

        Assert.NotNull(verdict);
        Assert.Equal("Plan A and Plan B are effectively the same overall", verdict.Text);
        Assert.Equal(ComparisonDirection.Neutral, verdict.Direction);
    }

    [Fact]
    public void AVerdictNamesItsScopeWhenThePlansDoNotHoldTheSameStatements()
    {
        var verdict = Compare(
            Plan(Statement(hash: "0xAA", elapsedMs: 100), Statement(hash: "0xBB", elapsedMs: 900, text: "select 2;")),
            Plan(Statement(hash: "0xAA", elapsedMs: 50)))
            .Verdict;

        Assert.NotNull(verdict);
        Assert.Equal("Plan B is 50.0% faster across the 1 statement both plans share", verdict.Text);
    }

    [Fact]
    public void NothingToMeasureMeansNoVerdictRatherThanAManufacturedOne()
    {
        // Nothing to be a percentage of.
        Assert.Null(Compare(Plan(Statement(cost: 0)), Plan(Statement(cost: 0))).Verdict);

        // Nothing at all.
        Assert.Null(Compare(Plan(), Plan()).Verdict);

        /* And nothing PAIRED. Note that two statements with different query hashes still pair,
           positionally, which is the fallback working as intended — it takes a plan with no
           counterpart at all to leave the verdict without a basis. */
        Assert.Null(Compare(Plan(Statement()), Plan()).Verdict);
    }

    [Fact]
    public void TwoPlansWithNoStatementsStillRenderTheirReport()
    {
        // The one text branch the fixture baseline cannot reach: no committed plan is empty.
        var text = ComparisonFormatter.Compare(Plan(), Plan(), "before", "after");

        Assert.Contains("No statements to compare.", text);
        Assert.Contains("Plan A: before", text);
        Assert.Empty(Compare(Plan(), Plan()).Statements);
    }

    // --- Ordering ------------------------------------------------------------------------------

    [Fact]
    public void WorstFirstPutsTheBiggestRegressionAtTheTopAndImprovementsAtTheBottom()
    {
        var statement = Compare(
            Plan(Statement(cost: 10, rows: 100, elapsedMs: 100, cpuMs: 100, warnings: 1)),
            Plan(Statement(cost: 12, rows: 100, elapsedMs: 900, cpuMs: 40, warnings: 3)))
            .Statements.Single();

        var ordered = ComparisonOrdering.WorstFirst(statement.Metrics);

        Assert.Equal(
            [
                "Runtime",          // 800% worse
                "Estimated cost",   //  20% worse
                "Warnings",         // worse, but a count has no percentage to rank by
                "Estimated rows",   // no verdict at all
                "CPU time"          // the one improvement
            ],
            ordered.Select(metric => metric.Label));
    }

    [Fact]
    public void WorstFirstLeavesTheVerdictlessMetricsInTheOrderTheReportPrintedThem()
    {
        /* An unchanged cost, a 50x row estimate and a DOP that went from serial to eight are all
           Neutral, for three different reasons. Sorting them against each other by size would
           shuffle three rows for no reason a reader could name, so they keep report order. */
        var statement = Compare(
            Plan(Statement(rows: 10, dop: 1)),
            Plan(Statement(rows: 5000, dop: 8)))
            .Statements.Single();

        var neutral = ComparisonOrdering.WorstFirst(statement.Metrics)
            .Where(metric => metric.Direction == ComparisonDirection.Neutral)
            .Select(metric => metric.Label);

        Assert.Equal(["Estimated cost", "Estimated rows", "DOP"], neutral);
    }

    // --- Construction --------------------------------------------------------------------------

    private static ComparisonMetric Metric(ComparisonResult result, string label) =>
        result.Statements.Single().Metrics.Single(metric => metric.Label == label);

    /// <summary>
    /// Builds under the invariant culture, because every display and delta string is produced here
    /// and half of them go through "N0"/"N1". A developer on a comma-decimal machine should not see
    /// a different suite than CI does.
    /// </summary>
    private static ComparisonResult Compare(AnalysisResult planA, AnalysisResult planB)
    {
        var previous = CultureInfo.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            return ComparisonFormatter.Build(planA, planB, "A", "B");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    private static AnalysisResult Plan(params StatementResult[] statements) =>
        new()
        {
            PlanSource = "test",
            Statements = statements.ToList(),
            Summary = new AnalysisSummary
            {
                TotalStatements = statements.Length,
                HasActualStats = statements.Any(statement => statement.QueryTime != null)
            }
        };

    private static StatementResult Statement(
        string hash = "0xAA",
        double cost = 1,
        double rows = 10,
        long? elapsedMs = null,
        long? cpuMs = null,
        long grantKb = 0,
        int dop = 0,
        int warnings = 0,
        int missingIndexes = 0,
        (string Type, long Ms)[]? waits = null,
        (long Logical, long Physical)? io = null,
        string text = "select 1;")
    {
        var statement = new StatementResult
        {
            StatementText = text,
            StatementType = "SELECT",
            QueryHash = hash,
            EstimatedCost = cost,
            EstimatedRows = rows,
            DegreeOfParallelism = dop
        };

        if (elapsedMs.HasValue || cpuMs.HasValue)
        {
            statement.QueryTime = new QueryTimeResult
            {
                ElapsedTimeMs = elapsedMs ?? 0,
                CpuTimeMs = cpuMs ?? elapsedMs ?? 0
            };
        }

        if (grantKb > 0)
            statement.MemoryGrant = new MemoryGrantResult { GrantedKB = grantKb, RequestedKB = grantKb };

        for (int i = 0; i < warnings; i++)
            statement.Warnings.Add(new WarningResult { Type = $"Warning{i}", Severity = "Warning", Message = "x" });

        for (int i = 0; i < missingIndexes; i++)
            statement.MissingIndexes.Add(new MissingIndexResult { Table = $"T{i}", Impact = 50 });

        foreach (var (type, ms) in waits ?? [])
            statement.WaitStats.Add(new WaitStatResult { WaitType = type, WaitTimeMs = ms, WaitCount = 1 });

        if (io.HasValue)
        {
            /* Split across a parent and a child on purpose: the reads are summed over the whole
               operator tree, and a single-node tree would pass whether they are or not. */
            statement.OperatorTree = new OperatorResult
            {
                NodeId = 0,
                PhysicalOp = "Select",
                ActualLogicalReads = io.Value.Logical / 4,
                ActualPhysicalReads = io.Value.Physical / 2,
                Children =
                {
                    new OperatorResult
                    {
                        NodeId = 1,
                        PhysicalOp = "Index Scan",
                        ActualLogicalReads = io.Value.Logical - (io.Value.Logical / 4),
                        ActualPhysicalReads = io.Value.Physical - (io.Value.Physical / 2)
                    }
                }
            };
        }

        return statement;
    }
}
