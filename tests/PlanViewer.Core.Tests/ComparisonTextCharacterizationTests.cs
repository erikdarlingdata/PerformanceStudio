using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// Golden master over <see cref="ComparisonFormatter.Compare"/>'s complete text output.
///
/// <para><b>Why this exists.</b> That text is not a private rendering detail — the
/// <c>compare_plans</c> MCP tool returns it verbatim to whatever model called it, and the
/// comparison report is copied out of the UI by hand. Giving the comparison a typed model so the
/// new diff window could colour it meant moving every format string, pad, guard and rounding rule
/// through a second layer, and "I kept it the same" is not a thing you can eyeball across 300
/// lines of padding arithmetic. So the bytes are recorded first, against the code as it was, and
/// the refactor has to reproduce them.</para>
///
/// <para><b>What it covers.</b> Every committed fixture compared against itself — which is the
/// only way to reach the "no change" and both-sides-equal branches on every metric at once — and
/// every fixture compared against the next one in name order, wrapping, which is what produces
/// mismatched statement counts, only-in-Plan-A/B blocks, the estimated-versus-actual note, and the
/// (new) / (eliminated) zero-side deltas. 84 comparisons in all.</para>
///
/// <para><b>Culture is pinned.</b> The report's numbers go through "N0", "N1" and "#,##0.####"
/// against <see cref="CultureInfo.CurrentCulture"/>, so a machine on a comma-decimal locale would
/// otherwise record — or fail against — a different baseline than CI.</para>
/// </summary>
public class ComparisonTextCharacterizationTests
{
    private const string BaselineFile = "ComparisonBaseline.txt";

    [Fact]
    public void EveryFixturePairing_StillRendersTheRecordedText()
    {
        var actual = RenderDigest();
        var baselinePath = Path.Combine(ProjectDir(), BaselineFile);

        if (!File.Exists(baselinePath))
        {
            /* Recorded, then failed on purpose. The sibling warning golden master returns green on
               the recording run, which is fine for output nobody is mid-refactor on and actively
               dangerous here: a refactor that deleted the baseline would re-record its own broken
               output and pass. A missing baseline is a thing to look at, not to paper over. */
            File.WriteAllText(baselinePath, actual);
            Assert.Fail(
                $"No {BaselineFile} was present, so the current comparison output has been recorded " +
                "into one. Check it into git if it is the intended text, then re-run.");
        }

        var expected = File.ReadAllText(baselinePath).Replace("\r\n", "\n");
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The pairings, rendered end to end into one document so a single assertion covers all of
    /// them and a diff points straight at the pairing that moved.
    /// </summary>
    private static string RenderDigest()
    {
        var names = Directory
            .GetFiles(Path.Combine(AppContext.BaseDirectory, "Plans"), "*.sqlplan")
            .Select(Path.GetFileName)
            .Where(name => name != null)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(names);

        var analyses = names.ToDictionary(name => name, Analyze, StringComparer.Ordinal);

        var previousCulture = CultureInfo.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var digest = new StringBuilder();

            foreach (var name in names)
                Append(digest, name, name, analyses);

            for (int i = 0; i < names.Count; i++)
                Append(digest, names[i], names[(i + 1) % names.Count], analyses);

            return digest.ToString().Replace("\r\n", "\n");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previousCulture;
        }
    }

    private static void Append(
        StringBuilder digest, string a, string b, IReadOnlyDictionary<string, AnalysisResult> analyses)
    {
        digest.Append("##### ").Append(a).Append(" vs ").Append(b).Append('\n');
        digest.Append(ComparisonFormatter.Compare(analyses[a], analyses[b], a, b));
        digest.Append('\n');
    }

    private static AnalysisResult Analyze(string planFile) =>
        ResultMapper.Map(PlanTestHelper.LoadAndAnalyze(planFile), planFile);

    private static string ProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlanViewer.Core.Tests.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
