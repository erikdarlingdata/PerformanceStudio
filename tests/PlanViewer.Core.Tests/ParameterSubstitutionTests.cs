using System.Collections.Generic;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #467: the statement text in a plan is what SQL Server compiled, which under
/// <c>PARAMETERIZATION FORCED</c> means every literal has already been replaced by <c>@0</c>,
/// <c>@1</c> … The values are in the plan's ParameterList, and these cover putting them back.
///
/// <para>The token boundary is the part worth being paranoid about. <c>@1</c> is a prefix of
/// <c>@11</c>, so a naive replace corrupts the statement instead of fixing it, and an <c>@0</c>
/// inside a string literal is data that must survive untouched.</para>
/// </summary>
public class ParameterSubstitutionTests
{
    private static PlanParameter Param(string name, string? compiled, string? runtime = null) =>
        new() { Name = name, DataType = "int", CompiledValue = compiled, RuntimeValue = runtime };

    [Fact]
    public void NumericValue_LosesItsParentheses()
    {
        var result = ParameterSubstitution.Apply(
            "WHERE [t].[StatusId] = @1",
            new List<PlanParameter> { Param("@1", "(5)") });

        Assert.Equal("WHERE [t].[StatusId] = 5", result.Text);
        Assert.Equal(1, result.SubstitutionCount);
    }

    [Fact]
    public void StringValue_KeepsItsQuotes()
    {
        var result = ParameterSubstitution.Apply(
            "WHERE [t0].[AuthUserId] = @0",
            new List<PlanParameter> { Param("@0", "'123456'") });

        Assert.Equal("WHERE [t0].[AuthUserId] = '123456'", result.Text);
    }

    [Fact]
    public void ShorterNameIsNotSubstitutedInsideALongerOne()
    {
        /* The corruption a naive Replace("@1", "5") produces: "@11" becomes "51". */
        var result = ParameterSubstitution.Apply(
            "WHERE a = @1 AND b = @11",
            new List<PlanParameter> { Param("@1", "(5)"), Param("@11", "(99)") });

        Assert.Equal("WHERE a = 5 AND b = 99", result.Text);
        Assert.Equal(2, result.SubstitutionCount);
    }

    [Fact]
    public void ShorterNameWithNoLongerCounterpartStillLeavesTheLongerNameAlone()
    {
        var result = ParameterSubstitution.Apply(
            "WHERE a = @1 AND b = @11",
            new List<PlanParameter> { Param("@1", "(5)") });

        Assert.Equal("WHERE a = 5 AND b = @11", result.Text);
        Assert.Equal(1, result.SubstitutionCount);
    }

    [Fact]
    public void NameInsideAStringLiteral_IsLeftAlone()
    {
        var result = ParameterSubstitution.Apply(
            "WHERE a = @0 AND note = 'sent to @0 by hand'",
            new List<PlanParameter> { Param("@0", "'x'") });

        Assert.Equal("WHERE a = 'x' AND note = 'sent to @0 by hand'", result.Text);
        Assert.Equal(1, result.SubstitutionCount);
    }

    [Fact]
    public void NameInsideAStringLiteralWithDoubledQuotes_IsStillLeftAlone()
    {
        /* The doubled quote does not end the literal, so @0 after it is still inside one. */
        var result = ParameterSubstitution.Apply(
            "WHERE note = 'it''s @0 already' AND a = @0",
            new List<PlanParameter> { Param("@0", "(7)") });

        Assert.Equal("WHERE note = 'it''s @0 already' AND a = 7", result.Text);
        Assert.Equal(1, result.SubstitutionCount);
    }

    [Fact]
    public void NameInsideADelimitedIdentifierOrComment_IsLeftAlone()
    {
        var result = ParameterSubstitution.Apply(
            "SELECT [@0] /* not @0 either */ FROM t -- and not @0\nWHERE a = @0",
            new List<PlanParameter> { Param("@0", "(1)") });

        Assert.Equal(
            "SELECT [@0] /* not @0 either */ FROM t -- and not @0\nWHERE a = 1",
            result.Text);
        Assert.Equal(1, result.SubstitutionCount);
    }

    [Fact]
    public void NameAtTheTailOfAnIdentifier_IsNotAParameterReference()
    {
        /* "t@0" is one identifier. Substituting the tail of it produces nonsense. */
        var result = ParameterSubstitution.Apply(
            "SELECT t@0 FROM x WHERE a = @0",
            new List<PlanParameter> { Param("@0", "(1)") });

        Assert.Equal("SELECT t@0 FROM x WHERE a = 1", result.Text);
        Assert.Equal(1, result.SubstitutionCount);
    }

    [Fact]
    public void GlobalVariable_IsNotMistakenForAParameter()
    {
        /* @@ROWCOUNT reads as one token, so a parameter named @ROWCOUNT cannot land inside it. */
        var result = ParameterSubstitution.Apply(
            "SELECT @@ROWCOUNT, @ROWCOUNT",
            new List<PlanParameter> { Param("@ROWCOUNT", "(3)") });

        Assert.Equal("SELECT @@ROWCOUNT, 3", result.Text);
        Assert.Equal(1, result.SubstitutionCount);
    }

    [Fact]
    public void NullValue_IsSubstituted()
    {
        var result = ParameterSubstitution.Apply(
            "WHERE a = @p",
            new List<PlanParameter> { Param("@p", "NULL") });

        Assert.Equal("WHERE a = NULL", result.Text);
        Assert.Equal(1, result.SubstitutionCount);
    }

    [Fact]
    public void RuntimeValueWins_WhenBothArePresent()
    {
        /* Compiled is what the plan was built for; runtime is what this execution passed, and the
           point of copying the statement out is to run the case in front of you. */
        var result = ParameterSubstitution.Apply(
            "WHERE a = @p",
            new List<PlanParameter> { Param("@p", "(1)", "(2)") });

        Assert.Equal("WHERE a = 2", result.Text);
    }

    [Fact]
    public void CompiledValueIsUsed_WhenRuntimeIsMissing()
    {
        /* @6 in the #466 repro has a compiled value and no runtime value. */
        var result = ParameterSubstitution.Apply(
            "WHERE a >= @6",
            new List<PlanParameter> { Param("@6", "'2026-05-28 10:28:07.3132561'") });

        Assert.Equal("WHERE a >= '2026-05-28 10:28:07.3132561'", result.Text);
    }

    [Fact]
    public void ParameterWithNoValueAtAll_IsLeftAlone()
    {
        /* OPTION(RECOMPILE) and local variables produce names without values. Inventing one would
           be worse than leaving the name where the user can see it. */
        var result = ParameterSubstitution.Apply(
            "WHERE a = @known AND b = @unknown",
            new List<PlanParameter> { Param("@known", "(1)"), Param("@unknown", null) });

        Assert.Equal("WHERE a = 1 AND b = @unknown", result.Text);
        Assert.Equal(1, result.SubstitutionCount);
    }

    [Fact]
    public void ParameterInTheListButNotInTheText_ChangesNothing()
    {
        var result = ParameterSubstitution.Apply(
            "SELECT 1",
            new List<PlanParameter> { Param("@unused", "(1)") });

        Assert.Equal("SELECT 1", result.Text);
        Assert.Equal(0, result.SubstitutionCount);
    }

    [Fact]
    public void MissingInputs_AreHandledRatherThanThrown()
    {
        /* Both nulls are reachable: a plan can carry a statement with no text at all, and callers
           hand over whatever the parser produced. */
        Assert.Equal("", ParameterSubstitution.Apply(null, new List<PlanParameter>()).Text);
        Assert.Equal("SELECT 1", ParameterSubstitution.Apply("SELECT 1", null).Text);
        Assert.Equal("SELECT 1", ParameterSubstitution.Apply("SELECT 1", new List<PlanParameter>()).Text);
    }

    [Fact]
    public void GuidValue_LosesShowplansBraces()
    {
        /* {guid'...'} is showplan notation, not T-SQL, and is a syntax error if pasted. */
        var result = ParameterSubstitution.Apply(
            "WHERE id = @g",
            new List<PlanParameter>
            {
                Param("@g", "{guid'6F9619FF-8B86-D011-B42D-00C04FC964FF'}")
            });

        Assert.Equal("WHERE id = '6F9619FF-8B86-D011-B42D-00C04FC964FF'", result.Text);
    }

    [Fact]
    public void ValueWhoseParenthesesAreNotAWrapper_KeepsThem()
    {
        /* "(a)+(b)" starts and ends with a parenthesis but is not wrapped in one pair; slicing the
           ends off produces text that no longer parses. */
        var result = ParameterSubstitution.Apply(
            "WHERE a = @p",
            new List<PlanParameter> { Param("@p", "(a)+(b)") });

        Assert.Equal("WHERE a = (a)+(b)", result.Text);
    }

    [Fact]
    public void ForcedParameterizationPlan_GetsItsLiteralsBack()
    {
        /* The #466 reproduction, end to end from the plan file: seven parameters the engine
           manufactured, none of them declared anywhere, so the copied statement does not run. */
        var plan = PlanTestHelper.LoadAndAnalyze("forced_parameterization_plan.sqlplan");
        var statement = PlanTestHelper.FirstStatement(plan);

        Assert.Equal(7, statement.Parameters.Count);
        Assert.Contains("@0", statement.StatementText);

        var result = ParameterSubstitution.Apply(statement.StatementText, statement.Parameters);

        Assert.Equal(7, result.SubstitutionCount);
        Assert.Contains("[t0].[AuthUserId]='123456'", result.Text);
        Assert.Contains("[t].[StatusId] in (5,6,7,8,9)", result.Text);
        Assert.Contains("[t].[FromDateTime]>='2026-05-28 10:28:07.3132561'", result.Text);

        /* The literal that was never parameterized is still a literal, and no manufactured
           parameter name survives anywhere in the text. */
        Assert.Contains("like 'kexin%'", result.Text);
        Assert.DoesNotContain("@", result.Text);
    }
}
