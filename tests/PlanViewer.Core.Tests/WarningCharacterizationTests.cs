using System;
using System.IO;
using System.Linq;
using System.Text;

namespace PlanViewer.Core.Tests;

/// <summary>
/// Golden-master characterization of the analyzer's complete warning output across
/// every committed test plan. Locks down the exact set, order, severity, and message
/// text of all warnings so structural refactors of PlanAnalyzer (e.g. extracting the
/// inline rules in AnalyzeNode/AnalyzeStatement into methods) can be proven behavior-
/// preserving. The baseline is recorded on first run into WarningBaseline.txt next to
/// this test; any later change in analyzer output fails the assertion.
/// </summary>
public class WarningCharacterizationTests
{
    [Fact]
    public void AllPlans_WarningDigest_MatchesBaseline()
    {
        var plansDir = Path.Combine(AppContext.BaseDirectory, "Plans");
        var files = Directory.GetFiles(plansDir, "*.sqlplan")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal);

        var sb = new StringBuilder();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            sb.Append("### ").Append(name).Append('\n');
            var plan = PlanTestHelper.LoadAndAnalyze(name);
            foreach (var w in PlanTestHelper.AllWarnings(plan))
            {
                var msg = (w.Message ?? string.Empty).Replace("\r\n", "\n").Replace("\n", "\\n");
                sb.Append(w.WarningType).Append(" | ").Append(w.Severity).Append(" | ").Append(msg).Append('\n');
            }
            sb.Append('\n');
        }
        var actual = sb.ToString().Replace("\r\n", "\n");

        var baselinePath = Path.Combine(ProjectDir(), "WarningBaseline.txt");
        if (!File.Exists(baselinePath))
        {
            File.WriteAllText(baselinePath, actual);
            return; // first run records the baseline
        }

        var expected = File.ReadAllText(baselinePath).Replace("\r\n", "\n");
        Assert.Equal(expected, actual);
    }

    private static string ProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlanViewer.Core.Tests.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
