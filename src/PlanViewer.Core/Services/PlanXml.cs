using System.Buffers;
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

    /// <summary>
    /// The longest namespace URI that can be declared. XDocument looks a namespace up by its
    /// whole URI each time the namespace changes from one name to the next, so a long URI used
    /// by alternating names costs its length again at every change: a 1.4M-character document
    /// took 25 seconds. Real plans use three namespaces, the longest 55 characters.
    /// </summary>
    internal const int MaxNamespaceLength = 256;

    /// <summary>
    /// The most attributes one element can have. XmlReader reads a whole start tag before it
    /// can say how many attributes the tag has, and a tag with about a million of them took it
    /// 10 to 40 seconds, so they are counted in the text first (<see cref="CheckAttributeCounts"/>).
    /// Real plans have at most 20 on one element.
    /// </summary>
    internal const int MaxAttributes = 1024;

    private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";

    /// <summary>What ends a step through a start tag: an attribute's "=", a quote, or the tag's end.</summary>
    private static readonly SearchValues<char> StartTagStops = SearchValues.Create("=\"'>");

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
    /// and throws before XDocument starts on XML that is too large, too deeply nested, or that
    /// passes one of the limits on an element's attributes.
    /// </summary>
    private static void CheckLimits(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        if (xml.Length > MaxCharacters)
            throw new XmlException(
                $"Plan XML exceeds the supported size limit of {MaxCharacters.ToString("N0", CultureInfo.InvariantCulture)} characters.");

        CheckAttributeCounts(xml);

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

            if (reader.NodeType == XmlNodeType.Element && reader.HasAttributes)
                CheckAttributes(reader);
        }
    }

    /// <summary>
    /// Counts the attributes of each start tag in the text and throws at the first tag with more
    /// than <see cref="MaxAttributes"/>, in one pass and before XmlReader reads anything. Every
    /// attribute has exactly one "=" outside its quoted value, so the count is the number of "="
    /// between a start tag's "&lt;" and its "&gt;" that are not inside quotes. Comments, CDATA
    /// sections, processing instructions, declarations and end tags are stepped over. Text that is
    /// not well formed stops the count, and XmlReader reports it.
    /// </summary>
    internal static void CheckAttributeCounts(string xml)
    {
        var text = xml.AsSpan();
        var i = 0;

        while (true)
        {
            var open = text[i..].IndexOf('<');
            if (open < 0)
                return;
            i += open + 1;

            var rest = text[i..];
            if (rest.StartsWith("!--", StringComparison.Ordinal))
                i = SkipPast(text, i + 3, "-->");
            else if (rest.StartsWith("![CDATA[", StringComparison.Ordinal))
                i = SkipPast(text, i + 8, "]]>");
            else if (rest.StartsWith("?", StringComparison.Ordinal))
                i = SkipPast(text, i + 1, "?>");
            else if (rest.StartsWith("!", StringComparison.Ordinal) || rest.StartsWith("/", StringComparison.Ordinal))
                i = SkipPast(text, i + 1, ">");
            else
                i = SkipStartTag(text, i);

            if (i < 0)
                return;
        }
    }

    /// <summary>
    /// Steps through one start tag from just after its "&lt;", counting its attributes. Returns
    /// the index after the tag's "&gt;", or -1 at the end of the text.
    /// </summary>
    private static int SkipStartTag(ReadOnlySpan<char> text, int i)
    {
        var attributes = 0;

        while (true)
        {
            var stop = text[i..].IndexOfAny(StartTagStops);
            if (stop < 0)
                return -1;
            i += stop;

            switch (text[i])
            {
                case '>':
                    return i + 1;

                case '=':
                    if (++attributes > MaxAttributes)
                        throw TooManyAttributes();
                    i++;
                    break;

                default:
                    var close = text[(i + 1)..].IndexOf(text[i]);
                    if (close < 0)
                        return -1;
                    i += close + 2;
                    break;
            }
        }
    }

    /// <summary>The index after the first <paramref name="end"/> at or past <paramref name="i"/>, or -1.</summary>
    private static int SkipPast(ReadOnlySpan<char> text, int i, string end)
    {
        if (i > text.Length)
            return -1;

        var found = text[i..].IndexOf(end, StringComparison.Ordinal);
        return found < 0 ? -1 : i + found + end.Length;
    }

    private static XmlException TooManyAttributes() =>
        new($"Plan XML has an element with more than {MaxAttributes.ToString("N0", CultureInfo.InvariantCulture)} attributes.");

    private static void CheckAttributes(XmlReader reader)
    {
        if (reader.AttributeCount > MaxAttributes)
            throw TooManyAttributes();

        while (reader.MoveToNextAttribute())
        {
            if (reader.NamespaceURI == XmlnsNamespace && reader.Value.Length > MaxNamespaceLength)
                throw new XmlException(
                    $"Plan XML declares a namespace longer than {MaxNamespaceLength.ToString("N0", CultureInfo.InvariantCulture)} characters.");
        }

        reader.MoveToElement();
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
