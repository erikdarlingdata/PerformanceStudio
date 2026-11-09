using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using PlanViewer.App.Controls;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The parameters card's all-compiled-null annotation (#679). Compiled values are absent under
/// OPTIMIZE FOR UNKNOWN, under OPTION (RECOMPILE), and when the plan was captured without them. The
/// card names a hint only when the statement carries it; otherwise it says the values were not
/// captured. local_variable_plan has one parameter with a runtime value and no compiled value, and
/// its statement has no hint.
/// </summary>
public class ParameterCardAnnotationTests
{
    private const string RecompileFree = "WHERE CreationDate &gt;= @date\" StatementId";
    private const string WithRecompile = "WHERE CreationDate &gt;= @date OPTION (RECOMPILE)\" StatementId";

    [Fact]
    public void AllCompiledNull_WithoutRecompileOrHint_SaysTheValuesWereNotCaptured()
    {
        HeadlessUi.Run(() =>
        {
            var texts = Annotations(LocalVariablePlanXml());

            Assert.Contains(texts, t => t.StartsWith("Parameter values not captured in the plan"));
            Assert.DoesNotContain(texts, t => t.Contains("OPTION(RECOMPILE)"));
        });
    }

    [Fact]
    public void AllCompiledNull_WithRecompile_SaysRecompile()
    {
        HeadlessUi.Run(() =>
        {
            var xml = LocalVariablePlanXml();
            Assert.Contains(RecompileFree, xml);
            var texts = Annotations(xml.Replace(RecompileFree, WithRecompile));

            Assert.Contains(texts, t => t.StartsWith("OPTION(RECOMPILE)"));
            Assert.DoesNotContain(texts, t => t.StartsWith("Parameter values not captured"));
        });
    }

    private static string LocalVariablePlanXml()
    {
        var path = Path.Combine("Plans", "local_variable_plan.sqlplan");
        Assert.True(File.Exists(path), $"Test plan not found: {path}");
        return File.ReadAllText(path).Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
    }

    /// <summary>
    /// The texts the card shows below its table, in order. The statements grid renders its selected
    /// statement, and the card with it, once the viewer is shown and laid out in a window.
    /// </summary>
    private static string[] Annotations(string planXml)
    {
        var viewer = new PlanViewerControl();
        Assert.True(viewer.LoadPlan(planXml, "local_variable_plan.sqlplan"), $"Plan failed to load: {viewer.LastLoadError}");
        var window = new Window { Content = viewer, Width = 1600, Height = 1000 };
        window.Show();
        window.UpdateLayout();

        var content = viewer.FindControl<StackPanel>("ParametersContent");
        Assert.NotNull(content);
        return content.Children.OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
    }
}
