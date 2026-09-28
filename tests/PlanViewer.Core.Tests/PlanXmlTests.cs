using System.Text;
using System.Xml;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// Every site that loads plan XML goes through PlanXml. These pin its limits: no DTD, a limit on
/// nesting depth, and a limit on the sum of all nodes' depths, which is what XDocument's load
/// time grows with.
/// </summary>
public class PlanXmlTests
{
    private const string WithDtd =
        "<!DOCTYPE ShowPlanXML [<!ENTITY e \"x\">]><ShowPlanXML>&e;</ShowPlanXML>";

    [Fact]
    public void ADtdIsRefused()
    {
        var error = Assert.Throws<XmlException>(() => PlanXml.Parse(WithDtd));

        Assert.Contains("DTD", error.Message);
        Assert.Contains("DTD", ShowPlanParser.Parse(WithDtd).ParseError);
    }

    [Fact]
    public async Task ADtdIsRefusedOnTheAsyncPath()
    {
        var plan = await ShowPlanParser.ParseAsync(WithDtd, TestContext.Current.CancellationToken);

        Assert.Contains("DTD", plan.ParseError);
    }

    [Fact]
    public void XmlAtTheDepthLimitLoads()
    {
        var document = PlanXml.Parse(Nested(PlanXml.MaxDepth));

        Assert.NotNull(document.Root);
    }

    [Fact]
    public void XmlPastTheDepthLimitIsRefused()
    {
        var error = Assert.Throws<XmlException>(() => PlanXml.Parse(Nested(PlanXml.MaxDepth + 1)));

        Assert.Contains("depth limit", error.Message);
    }

    [Fact]
    public async Task XmlPastTheDepthLimitIsAParseErrorOnBothParserPaths()
    {
        var xml = Nested(PlanXml.MaxDepth + 1);

        Assert.Contains("depth limit", ShowPlanParser.Parse(xml).ParseError);
        var plan = await ShowPlanParser.ParseAsync(xml, TestContext.Current.CancellationToken);
        Assert.Contains("depth limit", plan.ParseError);
    }

    [Fact]
    public void TooManyDeeplyNestedElementsAreRefused()
    {
        /* Each element is within the depth limit, but together their depths pass the sum
           limit: 1,000 levels, then empty elements one level further down. */
        const int chain = 1000;
        var leaves = (int)(PlanXml.MaxDepthSum / (chain + 1)) + 1;
        var xml = new StringBuilder();
        for (var level = 0; level <= chain; level++)
            xml.Append("<a>");
        for (var leaf = 0; leaf < leaves; leaf++)
            xml.Append("<b/>");
        for (var level = 0; level <= chain; level++)
            xml.Append("</a>");

        var error = Assert.Throws<XmlException>(() => PlanXml.Parse(xml.ToString()));

        Assert.Contains("deeply nested", error.Message);
    }

    /// <summary>XML whose deepest element is <paramref name="depth"/> levels below the root.</summary>
    private static string Nested(int depth)
    {
        var xml = new StringBuilder();
        for (var level = 0; level <= depth; level++)
            xml.Append("<a>");
        for (var level = 0; level <= depth; level++)
            xml.Append("</a>");
        return xml.ToString();
    }
}
