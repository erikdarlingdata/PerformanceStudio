using System.Linq;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #455: a plan captured around <c>EXEC &lt;procedure&gt;</c> analyzed as one statement, zero
/// warnings, zero cost, exit 0 — on a file containing dozens of statement plans.
///
/// <para>The reported diagnosis was that the parse never descended into the procedure. It is subtler
/// than that and the distinction decides the fix: the parser had always read <c>StoredProc</c>
/// sub-plans, but that code sat BELOW an early return taken when a statement carries no
/// <c>QueryPlan</c> of its own — which is exactly what an <c>EXEC</c> statement is. The descent
/// existed and was unreachable in the only case it was written for.</para>
///
/// <para>What made it dangerous is that the output was well-formed and plausible. Nothing said the
/// analysis had stopped early; it looked like a clean plan with nothing to report, which invites
/// "no warnings found" about a procedure that in fact has plenty.</para>
/// </summary>
public class StoredProcedurePlanTests
{
    /// <summary>
    /// The three numbers from the report, asserted together. Any one alone could be innocent; the
    /// combination — one statement, no warnings, no cost — is the signature of a parse that did not
    /// reach the real statements.
    /// </summary>
    [Fact]
    public void AnExecProcedurePlanAnalyzesTheProcedureBody()
    {
        var plan = PlanTestHelper.LoadAndAnalyze("exec_stored_procedure_plan.sqlplan");
        var result = ResultMapper.Map(plan, "exec_stored_procedure_plan.sqlplan");

        Assert.True(result.Summary.TotalStatements > 1,
            $"the procedure body's statements are missing (got {result.Summary.TotalStatements})");
        Assert.True(result.Summary.TotalWarnings > 0,
            "a body with a non-SARGable predicate and a table variable cannot be warning-free");
        Assert.True(result.Summary.MaxEstimatedCost > 0,
            "every statement carrying a plan has a cost; zero means none were seen");
    }

    /// <summary>
    /// The specific cause, pinned so a future edit cannot reintroduce it by moving sub-plan parsing
    /// back below the early return: the EXEC statement itself has no QueryPlan, and must still
    /// carry its body.
    /// </summary>
    [Fact]
    public void TheExecStatementHasNoPlanOfItsOwnAndStillCarriesItsBody()
    {
        var plan = PlanTestHelper.LoadAndAnalyze("exec_stored_procedure_plan.sqlplan");

        var exec = plan.Batches.SelectMany(b => b.Statements).Single();

        Assert.NotNull(exec.StoredProcPlan);
        Assert.NotEmpty(exec.StoredProcPlan!.Statements);
    }

    /// <summary>
    /// The traversal the analyzer, the mapper and the test helper now share. They each walked
    /// batch.Statements separately before, which is how the analyzer and the golden master came to
    /// have the same blind spot and neither could catch the other.
    /// </summary>
    [Fact]
    public void EnumerateAllReachesNestedStatements()
    {
        var plan = PlanTestHelper.LoadAndAnalyze("exec_stored_procedure_plan.sqlplan");

        var shallow = plan.Batches.SelectMany(b => b.Statements).Count();
        var all = PlanStatements.EnumerateAll(plan).Count();

        Assert.Equal(1, shallow);
        Assert.True(all > shallow, $"nested statements were not reached (shallow {shallow}, all {all})");
    }

    /// <summary>
    /// The outer statement comes back before its body. Order matters for output: a reader scanning
    /// the statement list should meet the EXEC before the statements it caused.
    /// </summary>
    [Fact]
    public void TheCallingStatementComesBeforeItsBody()
    {
        var plan = PlanTestHelper.LoadAndAnalyze("exec_stored_procedure_plan.sqlplan");

        var all = PlanStatements.EnumerateAll(plan).ToList();
        var exec = plan.Batches.SelectMany(b => b.Statements).Single();

        Assert.Same(exec, all[0]);
    }

    /// <summary>
    /// Plans without a procedure are untouched. The fix only ever adds statements that were being
    /// dropped, so a plain batch must enumerate exactly as it always did.
    /// </summary>
    [Theory]
    [InlineData("row_goal_plan.sqlplan")]
    [InlineData("key_lookup_plan.sqlplan")]
    [InlineData("spill_plan.sqlplan")]
    public void APlanWithNoProcedureEnumeratesUnchanged(string planFile)
    {
        var plan = PlanTestHelper.LoadAndAnalyze(planFile);

        Assert.Equal(
            plan.Batches.SelectMany(b => b.Statements).Count(),
            PlanStatements.EnumerateAll(plan).Count());
    }

    /// <summary>
    /// The half of #456 the review caught: the ANALYZER descended into the body, the SCORER did
    /// not, so body warnings existed with MaxBenefitPercent forever null. The UI sorts
    /// unquantified warnings below quantified ones, which buried every finding of an EXEC plan —
    /// present in the list, missing its "up to X%", ranked last.
    ///
    /// <para>Non-SARGable Predicate is the probe because on an estimated plan it is scored
    /// unconditionally (operator cost percent fallback), so the same warning on an outer
    /// statement always carries a value — null on a body statement can only mean the scorer
    /// never got there.</para>
    /// </summary>
    [Fact]
    public void ProcedureBodyWarningsAreBenefitScored()
    {
        var plan = PlanTestHelper.LoadAndAnalyze("exec_stored_procedure_plan.sqlplan");

        var bodyWarnings = PlanStatements.EnumerateAll(plan)
            .Skip(1) // everything after the EXEC lives inside the procedure body
            .SelectMany(PlanTestHelper.AllNodeWarnings)
            .Where(w => w.WarningType == "Non-SARGable Predicate")
            .ToList();

        Assert.NotEmpty(bodyWarnings);
        Assert.All(bodyWarnings, w => Assert.NotNull(w.MaxBenefitPercent));
    }

    /// <summary>
    /// The wait-stats half of the same scorer gap, on a synthetic plan because the fixture is an
    /// estimated plan and carries no wait stats: a body statement's waits were never scored and
    /// never emitted as "Wait:" warnings, so an actual EXEC plan lost its wait analysis entirely.
    /// Serial statement, 400ms of PAGEIOLATCH against 1000ms elapsed — the simple-ratio path —
    /// so the expected 40% is arithmetic, not a golden value.
    /// </summary>
    [Fact]
    public void ProcedureBodyWaitStatsAreScoredAndSurfaced()
    {
        var body = new PlanStatement
        {
            StatementText = "SELECT o.Id FROM dbo.Orders AS o;",
            QueryTimeStats = new QueryTimeInfo { CpuTimeMs = 100, ElapsedTimeMs = 1_000 },
            WaitStats = { new WaitStatInfo { WaitType = "PAGEIOLATCH_SH", WaitTimeMs = 400, WaitCount = 10 } }
        };
        var plan = new ParsedPlan
        {
            Batches =
            {
                new PlanBatch
                {
                    Statements =
                    {
                        new PlanStatement
                        {
                            StatementText = "EXEC dbo.Waits;",
                            StoredProcPlan = new FunctionPlanInfo
                            {
                                ProcName = "dbo.Waits",
                                Statements = { body }
                            }
                        }
                    }
                }
            }
        };

        BenefitScorer.Score(plan);

        var waitWarning = Assert.Single(body.PlanWarnings, w => w.WarningType == "Wait: PAGEIOLATCH_SH");
        Assert.NotNull(waitWarning.MaxBenefitPercent);
        Assert.Equal(40.0, waitWarning.MaxBenefitPercent!.Value, 1);
    }

    /// <summary>
    /// The other pass the review caught walking batch.Statements: a user's severity override
    /// applied to a warning on the outer batch and silently did not apply to the identical
    /// warning inside the body. Rule 12 is Non-SARGable Predicate, which the body carries; its
    /// default severity is asserted first so the Critical below can only have come from the
    /// override being applied, not from the rule itself.
    /// </summary>
    [Fact]
    public void SeverityOverridesReachProcedureBodyStatements()
    {
        static List<PlanWarning> BodyNonSargable(ParsedPlan plan) =>
            PlanStatements.EnumerateAll(plan)
                .Skip(1)
                .SelectMany(PlanTestHelper.AllNodeWarnings)
                .Where(w => w.WarningType == "Non-SARGable Predicate")
                .ToList();

        var byDefault = BodyNonSargable(PlanTestHelper.LoadAndAnalyze("exec_stored_procedure_plan.sqlplan"));
        Assert.NotEmpty(byDefault);
        Assert.All(byDefault, w => Assert.Equal(PlanWarningSeverity.Warning, w.Severity));

        var config = new AnalyzerConfig
        {
            Rules = new RulesConfig { SeverityOverrides = { [12] = "Critical" } }
        };
        var overridden = BodyNonSargable(
            PlanTestHelper.LoadAndAnalyzeWithConfig("exec_stored_procedure_plan.sqlplan", config));

        Assert.NotEmpty(overridden);
        Assert.All(overridden, w => Assert.Equal(PlanWarningSeverity.Critical, w.Severity));
    }

    /// <summary>
    /// The context-carrying enumeration exists for UIs that list body statements to a person:
    /// a row has to say which module its statement came from. It must be the SAME walk as
    /// EnumerateAll — same statements, same order — because two traversals that must agree is
    /// the arrangement that produced #455 in the first place.
    /// </summary>
    [Fact]
    public void BodyStatementsKnowTheirContainingModule()
    {
        var plan = PlanTestHelper.LoadAndAnalyze("exec_stored_procedure_plan.sqlplan");

        var entries = PlanStatements.EnumerateAllWithContainer(plan).ToList();

        Assert.True(entries.Count > 1);
        Assert.Null(entries[0].ContainerPath); // the EXEC itself lives in the outer batch
        Assert.All(entries.Skip(1), e => Assert.Equal("dbo.ReproProc", e.ContainerPath));

        Assert.Equal(
            PlanStatements.EnumerateAll(plan),
            entries.Select(e => e.Statement));
    }
}
