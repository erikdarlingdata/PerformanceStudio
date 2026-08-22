using System.Linq;
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
}
