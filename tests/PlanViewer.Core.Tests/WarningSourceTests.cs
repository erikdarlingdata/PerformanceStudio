using System.IO;
using System.Linq;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #436: a reader could not tell which warnings SQL Server itself raised and which ones we inferred,
/// because both arrive as a <see cref="PlanWarning"/> with nothing but type, severity and message.
///
/// The distinction matters more than presentation. A warning the engine wrote into the plan's own
/// &lt;Warnings&gt; element is a record of what happened when the query ran — it spilled, it converted,
/// it had no statistics. One of our rules is an inference from plan shape, and an inference can be
/// wrong about a particular plan in a way the engine's own record cannot be. #436 was itself an
/// example: we claimed a conversion prevented a seek on a plan SQL Server had raised no conversion
/// warning about at all.
/// </summary>
public class WarningSourceTests
{
    /// <summary>
    /// One plan carrying both kinds. "Implicit Conversion" is lifted from the PlanAffectingConvert
    /// element SQL Server wrote; "Non-SARGable Predicate" is Rule 12 reading the predicate text.
    /// </summary>
    [Fact]
    public void TheTwoKindsAreToldApartOnAPlanCarryingBoth()
    {
        var plan = PlanTestHelper.LoadAndAnalyze("convert_implicit_plan.sqlplan");

        Assert.All(
            PlanTestHelper.WarningsOfType(plan, "Implicit Conversion"),
            w => Assert.Equal(PlanWarningSource.SqlServer, w.Source));

        Assert.All(
            PlanTestHelper.WarningsOfType(plan, "Non-SARGable Predicate"),
            w => Assert.Equal(PlanWarningSource.PerformanceStudio, w.Source));
    }

    /// <summary>
    /// The stamp is applied once, at the single return of ParseWarningsFromElement, rather than at
    /// each construction — so this asserts the property that arrangement buys: across every committed
    /// plan, no warning type is ever produced as both kinds. A type appearing as both would mean a
    /// construction site got missed or an analyzer rule started claiming the engine's authority.
    /// </summary>
    [Fact]
    public void NoWarningTypeIsEverProducedAsBothKinds()
    {
        var plansDir = Path.Combine(AppContext.BaseDirectory, "Plans");
        var confusions =
            (from file in Directory.GetFiles(plansDir, "*.sqlplan")
             let plan = PlanTestHelper.LoadAndAnalyze(Path.GetFileName(file))
             from warning in PlanTestHelper.AllWarnings(plan)
             group warning.Source by warning.WarningType into byType
             where byType.Distinct().Count() > 1
             select byType.Key).ToList();

        Assert.True(confusions.Count == 0,
            "These types are attributed to both SQL Server and us: " + string.Join(", ", confusions));
    }

    /// <summary>
    /// Only the engine's warnings are tagged in rendered output. Tagging both would put a badge on
    /// every line, which carries no information — our own advice is what a reader already expects
    /// from a plan analyzer.
    /// </summary>
    [Fact]
    public void OnlyTheEnginesWarningsAreTaggedInTextOutput()
    {
        var plan = PlanTestHelper.LoadAndAnalyze("convert_implicit_plan.sqlplan");
        var result = ResultMapper.Map(plan, "convert_implicit_plan.sqlplan");

        var writer = new StringWriter();
        TextFormatter.WriteText(result, writer);
        var text = writer.ToString();

        var tagged = text.Split('\n').Where(l => l.Contains("[SQL Server]")).ToList();

        Assert.NotEmpty(tagged);
        Assert.All(tagged, line => Assert.Contains("Implicit Conversion", line));
        Assert.DoesNotContain("Non-SARGable Predicate [SQL Server]", text);
    }

    /// <summary>The JSON and MCP consumers get it as a field rather than having to parse the tag out.</summary>
    [Fact]
    public void TheJsonOutputCarriesTheSource()
    {
        var plan = PlanTestHelper.LoadAndAnalyze("convert_implicit_plan.sqlplan");
        var result = ResultMapper.Map(plan, "convert_implicit_plan.sqlplan");

        var all = result.Statements
            .SelectMany(s => s.Warnings.Concat(Flatten(s.OperatorTree)))
            .ToList();

        Assert.Contains(all, w => w.Type == "Implicit Conversion" && w.Source == nameof(PlanWarningSource.SqlServer));
        Assert.Contains(all, w => w.Type == "Non-SARGable Predicate" && w.Source == nameof(PlanWarningSource.PerformanceStudio));
        Assert.All(all, w => Assert.NotEqual("", w.Source));
    }

    private static System.Collections.Generic.IEnumerable<WarningResult> Flatten(OperatorResult? node)
    {
        if (node == null) yield break;
        foreach (var w in node.Warnings) yield return w;
        foreach (var child in node.Children)
            foreach (var w in Flatten(child)) yield return w;
    }
}
