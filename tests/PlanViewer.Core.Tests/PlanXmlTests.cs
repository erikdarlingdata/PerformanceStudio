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

    [Theory]
    [InlineData("xmlns:a")]
    [InlineData("xmlns")]
    public void ANamespacePastTheLengthLimitIsRefused(string declaration)
    {
        var uri = "urn:" + new string('u', PlanXml.MaxNamespaceLength);

        var error = Assert.Throws<XmlException>(() => PlanXml.Parse($"<r {declaration}=\"{uri}\"><x/></r>"));

        Assert.Contains("namespace", error.Message);
    }

    [Fact]
    public void ANamespaceAtTheLengthLimitLoads()
    {
        var uri = "urn:" + new string('u', PlanXml.MaxNamespaceLength - 4);

        Assert.NotNull(PlanXml.Parse($"<r xmlns=\"{uri}\"><x/></r>").Root);
    }

    [Fact]
    public void AnElementAtTheAttributeLimitLoads()
    {
        Assert.NotNull(PlanXml.Parse(WithAttributes(PlanXml.MaxAttributes)).Root);
    }

    [Fact]
    public void AnElementPastTheAttributeLimitIsRefused()
    {
        var error = Assert.Throws<XmlException>(() => PlanXml.Parse(WithAttributes(PlanXml.MaxAttributes + 1)));

        Assert.Contains("attributes", error.Message);
    }

    /// <summary>
    /// XmlReader took 10 to 40 seconds to read a start tag this size before it could count its
    /// attributes. Counted in the text first, the tag is refused in milliseconds; the bound here
    /// is only loose enough for a slow build machine.
    /// </summary>
    [Fact]
    public void AStartTagWithAMillionAttributesIsRefusedQuickly()
    {
        var xml = WithAttributes(1_000_000);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var error = Assert.Throws<XmlException>(() => PlanXml.Parse(xml));

        Assert.Contains("attributes", error.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Took {clock.Elapsed}.");
    }

    [Fact]
    public void AnEqualsSignInsideAValueIsNotCounted()
    {
        PlanXml.CheckAttributeCounts(WithAttributes(PlanXml.MaxAttributes, value: "a=b=c"));
    }

    [Theory]
    [InlineData("a>b")]
    [InlineData("a'b")]
    public void AValueDoesNotEndTheCount(string value)
    {
        var xml = WithAttributes(PlanXml.MaxAttributes + 1, value);

        Assert.Throws<XmlException>(() => PlanXml.CheckAttributeCounts(xml));
    }

    [Fact]
    public void SingleQuotedValuesAreCountedLikeDoubleQuotedOnes()
    {
        var xml = WithAttributes(PlanXml.MaxAttributes + 1).Replace('"', '\'');

        Assert.Throws<XmlException>(() => PlanXml.CheckAttributeCounts(xml));
    }

    [Theory]
    [InlineData("<r><!-- {0} --></r>")]
    [InlineData("<r><![CDATA[{0}]]></r>")]
    [InlineData("<?note {0} ?><r/>")]
    [InlineData("<r>{1}</r>")]
    public void MarkupThatIsNotAStartTagIsNotCounted(string format)
    {
        var tag = WithAttributes(PlanXml.MaxAttributes + 1);
        var xml = string.Format(format, tag, System.Net.WebUtility.HtmlEncode(tag));

        PlanXml.CheckAttributeCounts(xml);
    }

    [Theory]
    [InlineData("<a b=\"1")]
    [InlineData("<a b=")]
    [InlineData("<!-- open")]
    [InlineData("<![CDATA[ open")]
    [InlineData("<?open")]
    [InlineData("</a")]
    [InlineData("<")]
    [InlineData("")]
    public void UnfinishedTextEndsTheCountWithoutAnError(string xml)
    {
        PlanXml.CheckAttributeCounts(xml);
    }

    [Fact]
    public void EveryFixturePlanPassesTheCount()
    {
        var plans = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Plans"), "*.sqlplan");

        Assert.NotEmpty(plans);
        foreach (var plan in plans)
            PlanXml.CheckAttributeCounts(File.ReadAllText(plan));
    }

    [Fact]
    public void NullIsAnArgumentError()
    {
        Assert.Throws<ArgumentNullException>(() => PlanXml.Parse(null!));
    }

    /// <summary>One element with <paramref name="count"/> attributes, each set to <paramref name="value"/>.</summary>
    private static string WithAttributes(int count, string value = "")
    {
        var xml = new StringBuilder("<a");
        for (var i = 0; i < count; i++)
            xml.Append(" a").Append(i).Append("=\"").Append(value).Append('"');
        return xml.Append("/>").ToString();
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
