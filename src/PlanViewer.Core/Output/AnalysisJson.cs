using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlanViewer.Core.Output;

/// <summary>
/// How deep a serializer must be willing to go to write an <see cref="AnalysisResult"/>, in ONE place
/// because every writer of this object needs the same answer and none of them can tell when they got it
/// wrong.
///
/// <para><b>Why this exists (#430).</b> <see cref="System.Text.Json"/> defaults <c>MaxDepth</c> to 64 and
/// throws when a graph exceeds it. <see cref="OperatorResult.Children"/> nests once per operator, and each
/// level costs two JSON levels (the object, then the array), so a plan roughly 30 operators deep exhausts
/// the default. Clicking "Advice for Robots" on a large plan therefore threw a <c>JsonException</c> out of
/// an Avalonia click handler, which Avalonia does not guard, and the process died with no dialog and no
/// log entry the user could act on.</para>
///
/// <para><b>Why raising it is safe here, which is the part worth checking before copying this.</b> The
/// exception message reads "A possible object cycle was detected. This can either be due to a cycle or if
/// the object depth is larger than the maximum allowed depth of 64", and those two causes want opposite
/// fixes: raising the limit on a genuine cycle just moves the crash. The serialized graph is
/// <see cref="OperatorResult"/>, which has <c>Children</c> and no parent link, so it is a tree and the
/// depth reading is the right one. Note that the INTERNAL model this is mapped from,
/// <c>PlanViewer.Core.Models.PlanNode</c>, DOES carry a <c>Parent</c> back-reference that the parser
/// populates — so if anything ever serializes <c>PlanNode</c> directly, that is a real cycle and needs
/// <c>[JsonIgnore]</c> on <c>Parent</c> rather than a bigger number.</para>
///
/// <para><b>The number is headroom, not a guarantee</b>, and it is deliberately paired with a caller-side
/// guard. 1024 covers about 500 nested operators against the ~30 that used to fail, which is far past any
/// plan seen in the field; a plan deeper than that now surfaces as a message instead of terminating the
/// process, because a serialization limit should never be able to take the app down.</para>
/// </summary>
public static class AnalysisJson
{
    /// <summary>Depth ceiling for every serializer that writes an <see cref="AnalysisResult"/>.</summary>
    public const int MaxDepth = 1024;

    /// <summary>
    /// The indented options the UI writes advice with. Matches what the call sites built inline before
    /// #430 — <c>WriteIndented</c> only — so the emitted JSON is unchanged apart from no longer failing
    /// partway down a deep tree.
    /// </summary>
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        MaxDepth = MaxDepth,
    };

    /// <summary>
    /// What the CLI writes analysis files with — same as <see cref="Indented"/> plus dropping nulls,
    /// which is what keeps `analyze --json` output readable.
    ///
    /// <para>These live here rather than on the commands because the commands each built their own
    /// copy, and that is precisely how <c>querystore</c> was left behind when #430 was fixed: the
    /// depth ceiling was made a shared constant, but the OPTIONS were still duplicated, so adding
    /// <c>MaxDepth</c> to the two sets in AnalyzeCommand silently did nothing for the identical pair
    /// in QueryStoreCommand. A shared constant only helps the call sites that remember to reference
    /// it. Sharing the options instead is what makes the next command unable to get it wrong.</para>
    /// </summary>
    public static readonly JsonSerializerOptions IndentedWithoutNulls = new()
    {
        WriteIndented = true,
        MaxDepth = MaxDepth,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The unindented counterpart to <see cref="IndentedWithoutNulls"/>, for --compact.</summary>
    public static readonly JsonSerializerOptions CompactWithoutNulls = new()
    {
        MaxDepth = MaxDepth,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
