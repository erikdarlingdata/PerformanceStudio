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
    /// <paramref name="statements"/> and everything nested beneath them. Defined in terms of
    /// <see cref="EnumerateAllWithContainer(IReadOnlyList{PlanStatement})"/> so the plain and the
    /// context-carrying enumeration are literally the same walk — a consumer picking either one
    /// gets the same statements in the same order, which is the whole point of this class.
    /// </summary>
    public static IEnumerable<PlanStatement> EnumerateAll(IReadOnlyList<PlanStatement> statements)
    {
        foreach (var entry in EnumerateAllWithContainer(statements))
            yield return entry.Statement;
    }

    /// <summary>
    /// Every statement in the plan with the name of the module whose body it came from, for
    /// consumers that show statements to a person rather than just walking them.
    ///
    /// <para>Why the context matters (#456 follow-up): once the desktop statements grid started
    /// listing procedure-body statements alongside the outer batch, five rows of bare SELECTs with
    /// nothing saying which came from <c>EXEC dbo.Whatever</c> would be technically complete and
    /// practically unreadable. The container name is part of the traversal rather than something
    /// each UI reconstructs with its own walk, because two walks that must agree is exactly the
    /// arrangement that produced #455.</para>
    /// </summary>
    public static IEnumerable<StatementWithContainer> EnumerateAllWithContainer(ParsedPlan plan)
    {
        foreach (var batch in plan.Batches)
        {
            foreach (var entry in EnumerateAllWithContainer(batch.Statements))
                yield return entry;
        }
    }

    /// <summary>
    /// <paramref name="statements"/> and everything nested beneath them, each paired with the
    /// module path it lives in (null for the outer batch).
    ///
    /// <para>An explicit stack rather than recursion, because procedure bodies nest — a procedure
    /// calling a procedure calling a function — and #430 was a crash caused by assuming a plan's
    /// shapes are shallow.</para>
    /// </summary>
    public static IEnumerable<StatementWithContainer> EnumerateAllWithContainer(IReadOnlyList<PlanStatement> statements)
    {
        var pending = new Stack<StatementWithContainer>();
        for (var i = statements.Count - 1; i >= 0; i--)
            pending.Push(new StatementWithContainer(statements[i], ContainerPath: null));

        while (pending.TryPop(out var entry))
        {
            yield return entry;

            /* Pushed in reverse so the bodies come back out in source order, and pushed AFTER the
               statement is yielded so a body follows the EXEC that owns it rather than preceding it. */
            var statement = entry.Statement;
            for (var i = statement.UdfPlans.Count - 1; i >= 0; i--)
                PushAll(statement.UdfPlans[i], entry.ContainerPath, pending);

            if (statement.StoredProcPlan is not null)
                PushAll(statement.StoredProcPlan, entry.ContainerPath, pending);
        }
    }

    private static void PushAll(FunctionPlanInfo body, string? outerPath, Stack<StatementWithContainer> pending)
    {
        var path = AppendModule(outerPath, body.ProcName);
        for (var i = body.Statements.Count - 1; i >= 0; i--)
            pending.Push(new StatementWithContainer(body.Statements[i], path));
    }

    /// <summary>
    /// Chains module names for nesting, so a statement two bodies deep reads
    /// "dbo.Outer &gt; dbo.Inner" rather than pretending it sits directly in dbo.Inner's caller.
    /// A module the plan left unnamed contributes nothing to the path — the traversal reports what
    /// the plan said, not a placeholder — so its statements inherit the enclosing path (or none).
    /// </summary>
    private static string? AppendModule(string? outerPath, string procName)
    {
        if (string.IsNullOrEmpty(procName))
            return outerPath;
        return outerPath is null ? procName : outerPath + " > " + procName;
    }
}

/// <summary>
/// One statement from <see cref="PlanStatements.EnumerateAllWithContainer(ParsedPlan)"/>:
/// the statement itself, and the proc/UDF module path it came from — null for a statement in the
/// outer batch, "dbo.Proc" for a procedure body, "dbo.Outer &gt; dbo.Inner" for nested bodies.
/// </summary>
public readonly record struct StatementWithContainer(PlanStatement Statement, string? ContainerPath);
