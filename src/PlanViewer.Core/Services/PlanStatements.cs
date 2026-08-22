using System.Collections.Generic;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

/// <summary>
/// Walks every statement in a plan, including the ones nested inside a stored procedure or a
/// user-defined function (#455).
///
/// <para><b>Why this exists.</b> A plan captured around <c>EXEC &lt;procedure&gt;</c> puts the
/// procedure's statements under a <c>StoredProc</c> element, and the parser has always read them —
/// into <see cref="PlanStatement.StoredProcPlan"/>. Nothing downstream looked there. The analyzer and
/// the result mapper both walked <c>batch.Statements</c> only, so a plan with eighty-seven statement
/// plans inside a procedure analyzed as one statement, zero warnings, zero cost, and exited 0. The
/// output was well-formed and entirely wrong, which is the worst way for this to fail.</para>
///
/// <para>The traversal itself was not missing — <c>PlanOperations.ValidateComplexity</c> has always
/// descended, which is how the complexity limit counted statements the analysis never saw. This puts
/// that same walk in one place so the two cannot disagree again.</para>
/// </summary>
public static class PlanStatements
{
    /// <summary>
    /// Every statement in the plan, outermost first, each nested body following the statement that
    /// owns it.
    /// </summary>
    public static IEnumerable<PlanStatement> EnumerateAll(ParsedPlan plan)
    {
        foreach (var batch in plan.Batches)
        {
            foreach (var statement in EnumerateAll(batch.Statements))
                yield return statement;
        }
    }

    /// <summary>
    /// <paramref name="statements"/> and everything nested beneath them.
    ///
    /// <para>An explicit stack rather than recursion, because procedure bodies nest — a procedure
    /// calling a procedure calling a function — and #430 was a crash caused by assuming a plan's
    /// shapes are shallow.</para>
    /// </summary>
    public static IEnumerable<PlanStatement> EnumerateAll(IReadOnlyList<PlanStatement> statements)
    {
        var pending = new Stack<PlanStatement>();
        for (var i = statements.Count - 1; i >= 0; i--)
            pending.Push(statements[i]);

        while (pending.TryPop(out var statement))
        {
            yield return statement;

            /* Pushed in reverse so the bodies come back out in source order, and pushed AFTER the
               statement is yielded so a body follows the EXEC that owns it rather than preceding it. */
            for (var i = statement.UdfPlans.Count - 1; i >= 0; i--)
                PushAll(statement.UdfPlans[i].Statements, pending);

            if (statement.StoredProcPlan is not null)
                PushAll(statement.StoredProcPlan.Statements, pending);
        }
    }

    private static void PushAll(IReadOnlyList<PlanStatement> statements, Stack<PlanStatement> pending)
    {
        for (var i = statements.Count - 1; i >= 0; i--)
            pending.Push(statements[i]);
    }
}
