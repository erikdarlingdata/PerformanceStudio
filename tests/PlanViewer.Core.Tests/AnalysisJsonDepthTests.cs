using System.Linq;
using System.Text.Json;
using PlanViewer.Core.Output;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #430: "Robot Advice" crashed the app on a large plan.
///
/// System.Text.Json defaults MaxDepth to 64 and throws past it. An operator tree nests once per
/// operator and each level costs two JSON levels (the object, then the Children array), so a plan
/// roughly 30 operators deep exhausts the default. The throw came out of an Avalonia click handler,
/// which Avalonia does not guard, so the process died with no dialog and nothing actionable logged.
///
/// The reported stack named the cause precisely: the path was
/// $.Statements.OperatorTree.Children.Children...(30 deep)...NodeId — thirty CONSECUTIVE Children.
/// That matters, because the exception message offers two causes ("either be due to a cycle or if the
/// object depth is larger than the maximum allowed depth of 64") and they want opposite fixes. A
/// consecutive descent is depth. A cycle would have alternated, and raising the ceiling on a cycle
/// only moves the crash.
/// </summary>
public class AnalysisJsonDepthTests
{
    /// <summary>An analysis whose statement carries a single chain of nested operators.</summary>
    private static AnalysisResult WithOperatorChain(int operators)
    {
        var node = new OperatorResult { PhysicalOp = "Leaf" };
        for (var i = 1; i < operators; i++)
            node = new OperatorResult { PhysicalOp = "Nested", Children = { node } };

        return new AnalysisResult { Statements = { new StatementResult { OperatorTree = node } } };
    }

    /// <summary>
    /// The defect, reproduced against the options the call sites used to build inline. Without this the
    /// fix below is unfalsifiable — a passing serialize proves nothing if nothing ever failed.
    /// </summary>
    [Fact]
    public void TheOldInlineOptionsFailOnADeepPlan()
    {
        var exception = Assert.Throws<JsonException>(() =>
            JsonSerializer.Serialize(WithOperatorChain(60), new JsonSerializerOptions { WriteIndented = true }));

        Assert.Contains("depth", exception.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The fix: the shared options carry the plan that used to crash the app.</summary>
    [Theory]
    [InlineData(60)]
    [InlineData(200)]
    [InlineData(400)]
    public void TheSharedOptionsCarryADeepPlan(int operators)
    {
        var json = JsonSerializer.Serialize(WithOperatorChain(operators), AnalysisJson.Indented);

        Assert.Contains("\"Leaf\"", json, System.StringComparison.Ordinal);
        /* Every level actually made it out, rather than the serializer stopping quietly partway. */
        Assert.Equal(operators - 1, System.Text.RegularExpressions.Regex.Matches(json, "\"Nested\"").Count);
    }

    /// <summary>
    /// The ceiling is headroom, not a guarantee — which is exactly why the two UI call sites also catch.
    /// A plan past it must still fail as an exception the caller can turn into a message, not as
    /// silently truncated JSON that reads like a complete plan.
    /// </summary>
    [Fact]
    public void PastTheCeilingItStillThrowsRatherThanTruncating()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Serialize(WithOperatorChain(AnalysisJson.MaxDepth + 10), AnalysisJson.Indented));
    }

    /// <summary>
    /// The miss that #430's original fix left behind, and the reason the OPTIONS are shared and not
    /// just the constant.
    ///
    /// <para>Every CLI command that writes an analysis built its own JsonSerializerOptions. Making
    /// MaxDepth a shared constant only fixed the sets that were edited to reference it — AnalyzeCommand's
    /// two — while the identical pair in QueryStoreCommand kept the default ceiling of 64 and kept
    /// failing on any plan deeper than ~30 operators. It failed quietly there: the per-plan catch turns
    /// it into one "ERROR" row in summary.txt instead of an analysis, which is exactly the kind of
    /// wrong-but-not-loud result nobody files a bug about.</para>
    ///
    /// <para>So this walks the options rather than trusting the call sites, and a new command that
    /// rolls its own will fail here rather than in someone's Query Store sweep.</para>
    /// </summary>
    [Fact]
    public void EveryCliCommandWritesAnalysesWithTheCeiling()
    {
        var offenders =
            (from type in typeof(PlanViewer.Cli.Commands.AnalyzeCommand).Assembly.GetTypes()
             where type.Name.EndsWith("Command", System.StringComparison.Ordinal)
             from field in type.GetFields(System.Reflection.BindingFlags.NonPublic
                                        | System.Reflection.BindingFlags.Public
                                        | System.Reflection.BindingFlags.Static)
             where field.FieldType == typeof(JsonSerializerOptions)
             let options = (JsonSerializerOptions?)field.GetValue(null)
             where options is not null && options.MaxDepth != AnalysisJson.MaxDepth
             select $"{type.Name}.{field.Name} (MaxDepth {options.MaxDepth})").ToList();

        Assert.True(offenders.Count == 0,
            "These write an analysis with the default depth ceiling and will fail on a deep plan: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// Pins the headroom itself. 1024 is about 500 nested operators against the ~30 that used to fail;
    /// a future edit dropping it back toward the default would re-open #430 for large plans only, which
    /// is the shape of bug that reaches users rather than tests.
    ///
    /// <para>The Wire and Document pins matter beyond this assembly: server/PlanShare cannot
    /// reference PlanViewer.Core and mirrors this number as a literal (a shared constant only
    /// helps call sites that reference it), so this test is the tripwire that a change here means
    /// changing the server too.</para>
    /// </summary>
    [Fact]
    public void TheCeilingIsFarAboveAnyRealPlan()
    {
        Assert.Equal(1024, AnalysisJson.MaxDepth);
        Assert.Equal(AnalysisJson.MaxDepth, AnalysisJson.Indented.MaxDepth);
        Assert.True(AnalysisJson.Indented.WriteIndented, "advice output is read by people as well as models");
        Assert.Equal(AnalysisJson.MaxDepth, AnalysisJson.Wire.MaxDepth);
        Assert.Equal(AnalysisJson.MaxDepth, AnalysisJson.Document.MaxDepth);
        Assert.False(AnalysisJson.Wire.WriteIndented,
            "the share wire format has always been default-formatted JSON; only the ceiling changed");
    }

    /// <summary>
    /// The three writers #431 missed, found in review: the web Share upload serialized the
    /// analysis with inline default options, and loading a share back parsed AND deserialized it
    /// at the default 64 again — so a deep plan analyzed fine on screen and then failed to Share,
    /// or shared and failed to open. This walks the exact envelope shape the share path uses
    /// ({result, text, ttl_days} → JsonDocument → GetRawText → Deserialize) through the shared
    /// options, at 100 operators — past the old ceiling, far under the new one.
    ///
    /// <para>Scope, stated honestly: the actual call sites live in PlanViewer.Web (a Blazor WASM
    /// project this suite does not reference) and server/PlanShare (which references nothing), so
    /// they are verified by inspection to use these options / mirror this constant. What this test
    /// pins is the contract those sites rely on — that the shared options round-trip the envelope
    /// both directions.</para>
    /// </summary>
    [Fact]
    public void TheShareEnvelopeRoundTripsADeepPlan()
    {
        var payload = JsonSerializer.Serialize(new
        {
            result = WithOperatorChain(100),
            text = "=== Summary ===",
            ttl_days = 7
        }, AnalysisJson.Wire);

        /* Both proofs that the options are load-bearing: the default reader rejects what the
           shared writer produced (ThrowsAny because document parsing surfaces the depth failure
           as JsonReaderException, a JsonException subclass)... */
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(payload));

        /* ...and the shared reader carries it, all the way back to a typed result. */
        using var doc = JsonDocument.Parse(payload, AnalysisJson.Document);
        var result = JsonSerializer.Deserialize<AnalysisResult>(
            doc.RootElement.GetProperty("result").GetRawText(), AnalysisJson.Wire);

        Assert.NotNull(result);
        var depth = 0;
        for (var op = result!.Statements.Single().OperatorTree; op is not null; op = op.Children.FirstOrDefault())
            depth++;
        Assert.Equal(100, depth);
    }
}
