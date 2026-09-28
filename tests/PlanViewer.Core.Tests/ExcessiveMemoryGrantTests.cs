using System;
using System.IO;
using System.Linq;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// Rule 9's excessive-grant check. A grant of 1 GB or more that used none of it is the worst waste
/// there is, and the check used to skip it because it only looked at plans where MaxUsedMemory was
/// above 0. The other half of the fix is just as important: a plan that has no MaxUsedMemory at all
/// (an estimated plan, or one with no runtime grant info) must not read as "used nothing".
///
/// memory_grant_wait_plan.sqlplan has a 10,851,312 KB grant, so it clears the 1 GB floor. Each test
/// edits its MemoryGrantInfo attributes in the XML text, so the parser and the analyzer are both in
/// the path.
/// </summary>
public class ExcessiveMemoryGrantTests
{
    private const string Fixture = "memory_grant_wait_plan.sqlplan";
    private const string Granted = "GrantedMemory=\"10851312\"";
    private const string MaxUsed = "MaxUsedMemory=\"10232840\"";

    private static ParsedPlan Analyze(Func<string, string> editXml)
    {
        var path = Path.Combine("Plans", Fixture);
        Assert.True(File.Exists(path), $"Test plan not found: {path}");
        var xml = File.ReadAllText(path).Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
        Assert.Contains(Granted, xml);
        Assert.Contains(MaxUsed, xml);

        var plan = ShowPlanParser.Parse(editXml(xml));
        PlanAnalyzer.Analyze(plan);
        return plan;
    }

    private static PlanWarning[] Excessive(ParsedPlan plan) =>
        PlanTestHelper.WarningsOfType(plan, "Excessive Memory Grant").ToArray();

    [Fact]
    public void GrantThatUsedNothing_Fires()
    {
        var plan = Analyze(xml => xml.Replace(MaxUsed, "MaxUsedMemory=\"0\""));

        var warnings = Excessive(plan);
        var warning = Assert.Single(warnings);
        Assert.Equal(9, warning.RuleNumber);
        Assert.Equal(PlanWarningSeverity.Warning, warning.Severity);
        Assert.Contains("used none of it", warning.Message);
    }

    [Fact]
    public void GrantThatUsedNothing_MessageHasNoRatio()
    {
        var plan = Analyze(xml => xml.Replace(MaxUsed, "MaxUsedMemory=\"0\""));

        var message = Assert.Single(Excessive(plan)).Message;
        Assert.DoesNotContain("overestimate", message);
        Assert.DoesNotContain("only used", message);
        Assert.DoesNotContain("Infinity", message);
        Assert.DoesNotContain("NaN", message);
    }

    [Fact]
    public void MaxUsedMemoryMissingFromXml_DoesNotFire()
    {
        // No MaxUsedMemory attribute, as in an estimated plan or a plan with no runtime grant info.
        // The parser reports 0 for it, and 0 there means "not reported", not "used nothing".
        var plan = Analyze(xml => xml.Replace(" " + MaxUsed, ""));

        Assert.Empty(Excessive(plan));
    }

    [Fact]
    public void MaxUsedMemoryMissingFromXml_ParserSaysItWasNotReported()
    {
        var absent = Analyze(xml => xml.Replace(" " + MaxUsed, ""));
        var zero = Analyze(xml => xml.Replace(MaxUsed, "MaxUsedMemory=\"0\""));
        var present = Analyze(xml => xml);

        Assert.False(PlanTestHelper.FirstStatement(absent).MemoryGrant!.HasMaxUsedMemory);
        Assert.True(PlanTestHelper.FirstStatement(zero).MemoryGrant!.HasMaxUsedMemory);
        Assert.True(PlanTestHelper.FirstStatement(present).MemoryGrant!.HasMaxUsedMemory);
    }

    [Fact]
    public void GrantThatUsedNothing_JustUnderOneGb_DoesNotFire()
    {
        var plan = Analyze(xml => xml
            .Replace(Granted, "GrantedMemory=\"1048575\"")
            .Replace(MaxUsed, "MaxUsedMemory=\"0\""));

        Assert.Empty(Excessive(plan));
    }

    [Fact]
    public void GrantThatUsedNothing_AtOneGbExactly_Fires()
    {
        var plan = Analyze(xml => xml
            .Replace(Granted, "GrantedMemory=\"1048576\"")
            .Replace(MaxUsed, "MaxUsedMemory=\"0\""));

        Assert.Single(Excessive(plan));
    }

    [Fact]
    public void GrantThatUsedALittle_StillReportsTheRatio()
    {
        // 10,851,312 KB granted, 1,000 KB used: the old ratio message, unchanged.
        var plan = Analyze(xml => xml.Replace(MaxUsed, "MaxUsedMemory=\"1000\""));

        var message = Assert.Single(Excessive(plan)).Message;
        Assert.Contains("only used", message);
        Assert.Contains("x overestimate", message);
        Assert.DoesNotContain("used none of it", message);
    }

    [Fact]
    public void GrantThatUsedMostOfIt_DoesNotFire()
    {
        // The fixture as shipped: 10,851,312 KB granted, 10,232,840 KB used.
        var plan = Analyze(xml => xml);

        Assert.Empty(Excessive(plan));
    }

    [Fact]
    public void GrantUnderTenTimesTheUse_DoesNotFire()
    {
        // 5.4x, under the 10x floor.
        var plan = Analyze(xml => xml.Replace(MaxUsed, "MaxUsedMemory=\"2000000\""));

        Assert.Empty(Excessive(plan));
    }
}
