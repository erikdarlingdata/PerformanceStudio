using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace PlanViewer.Core.Services;

/// <summary>
/// Loads plan XML the same way everywhere. A plan can come from a file, the clipboard, a shared
/// link or an MCP call, so it is read with no DTD and no resolver, and within limits on its size
/// and nesting. A limit that is passed throws an <see cref="XmlException"/>, as malformed XML
/// does, so every caller already handles it.
/// </summary>
internal static class PlanXml
{
    /// <summary>The most characters plan XML can have.</summary>
    internal const int MaxCharacters = 16 * 1024 * 1024;

    /// <summary>
    /// How deep an element can be nested: 8 levels for each of the
    /// <see cref="ShowPlanParser.MaxParseDepth"/> levels the parser accepts. The deepest real
    /// plan measured nests 75 levels.
    /// </summary>
    internal const int MaxDepth = 8 * 1024;

    /// <summary>
    /// The most that the depths of all nodes can add up to. XDocument checks each node it adds
    /// against every ancestor, so its load time grows with this sum, not with the size of the
    /// XML: a few elements nested tens of thousands deep took minutes to load. At this limit a
    /// load takes under a second. The largest real plans measured come to about 100,000.
    /// </summary>
    internal const long MaxDepthSum = 1L << 29;

    internal static XDocument Parse(string xml)
    {
        CheckLimits(xml);
        using var reader = XmlReader.Create(new StringReader(xml), ReaderSettings(async: false));
        return XDocument.Load(reader);
    }

    internal static async Task<XDocument> ParseAsync(string xml, CancellationToken cancellationToken)
    {
        CheckLimits(xml);
        using var reader = XmlReader.Create(new StringReader(xml), ReaderSettings(async: true));
        return await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the XML once without building anything, which takes time in step with its length,
    /// and throws before XDocument starts on XML that is too large or too deeply nested.
    /// </summary>
    private static void CheckLimits(string xml)
    {
        if (xml.Length > MaxCharacters)
            throw new XmlException(
                $"Plan XML exceeds the supported size limit of {MaxCharacters.ToString("N0", CultureInfo.InvariantCulture)} characters.");

        using var reader = XmlReader.Create(new StringReader(xml), ReaderSettings(async: false));
        long depthSum = 0;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement)
                continue;

            if (reader.Depth > MaxDepth)
                throw new XmlException(
                    $"Plan XML exceeds the supported depth limit of {MaxDepth.ToString("N0", CultureInfo.InvariantCulture)} levels.");

            depthSum += reader.Depth;
            if (depthSum > MaxDepthSum)
                throw new XmlException("Plan XML has too many deeply nested elements.");
        }
    }

    /// <summary>
    /// The settings XDocument.Parse uses, except that a DTD is refused rather than processed
    /// (plans never have one) and the size limit applies.
    /// </summary>
    private static XmlReaderSettings ReaderSettings(bool async) => new()
    {
        Async = async,
        DtdProcessing = DtdProcessing.Prohibit,
        IgnoreWhitespace = true,
        MaxCharactersInDocument = MaxCharacters,
        XmlResolver = null
    };
}
