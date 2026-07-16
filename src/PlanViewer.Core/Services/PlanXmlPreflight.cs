using System.Security.Cryptography;
using System.Xml;

namespace PlanViewer.Core.Services;

internal readonly record struct PlanXmlPreflightResult(
    int StatementCount,
    int OperatorCount,
    int ElementCount,
    int AttributeCount,
    byte[] ContentHash);

internal static class PlanXmlPreflight
{
    internal const int DefaultMaxXmlDepth = 512;
    internal const int DefaultMaxXmlElements = 250_000;
    internal const int DefaultMaxXmlAttributes = 1_000_000;

    private const string ShowPlanNamespace =
        "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    internal static async Task<PlanXmlPreflightResult> ValidateAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("The plan stream must be readable and seekable.", nameof(stream));

        var settings = new XmlReaderSettings
        {
            Async = true,
            CloseInput = false,
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = PlanOperations.DefaultMaxPlanFileBytes,
            XmlResolver = null
        };

        var ancestry = new Stack<(string LocalName, string NamespaceUri)>();
        var statements = 0;
        var operators = 0;
        var elements = 0;
        var attributes = 0;
        stream.Position = 0;
        try
        {
            using var sha256 = SHA256.Create();
            await using var hashingStream = new CryptoStream(
                stream,
                sha256,
                CryptoStreamMode.Read,
                leaveOpen: true);
            using var reader = XmlReader.Create(hashingStream, settings);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stream.Length > PlanOperations.DefaultMaxPlanFileBytes ||
                    stream.Position > PlanOperations.DefaultMaxPlanFileBytes)
                {
                    throw new InvalidDataException(
                        $"Plan file exceeds the {PlanOperations.DefaultMaxPlanFileBytes / (1024 * 1024)} MiB size limit.");
                }

                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    ancestry.Pop();
                    continue;
                }

                if (reader.NodeType != XmlNodeType.Element)
                    continue;
                if (reader.Depth > DefaultMaxXmlDepth)
                {
                    throw new InvalidDataException(
                        $"Plan XML exceeds the supported depth limit of {DefaultMaxXmlDepth}.");
                }

                elements++;
                if (elements > DefaultMaxXmlElements)
                {
                    throw new InvalidDataException(
                        $"Plan XML exceeds the supported element limit of {DefaultMaxXmlElements}.");
                }

                attributes = checked(attributes + reader.AttributeCount);
                if (attributes > DefaultMaxXmlAttributes)
                {
                    throw new InvalidDataException(
                        $"Plan XML exceeds the supported attribute limit of {DefaultMaxXmlAttributes}.");
                }

                var parent = ancestry.TryPeek(out var value) ? value : default;
                var isShowPlanElement = reader.NamespaceURI.Equals(ShowPlanNamespace, StringComparison.Ordinal);
                var isDirectStatement =
                    parent.LocalName == "Statements" &&
                    parent.NamespaceUri == ShowPlanNamespace;
                var isCursorOperation =
                    isShowPlanElement &&
                    reader.LocalName == "Operation" &&
                    parent.LocalName == "CursorPlan" &&
                    parent.NamespaceUri == ShowPlanNamespace;
                var isFallbackStatement =
                    isShowPlanElement &&
                    reader.LocalName == "StmtSimple" &&
                    !isDirectStatement;
                if (isDirectStatement || isCursorOperation || isFallbackStatement)
                {
                    statements++;
                    if (statements > PlanOperations.DefaultMaxStatements)
                    {
                        throw new InvalidDataException(
                            $"Plan exceeds the {PlanOperations.DefaultMaxStatements} statement complexity limit.");
                    }
                }

                if (isShowPlanElement && reader.LocalName == "RelOp")
                {
                    operators++;
                    if (operators > PlanOperations.DefaultMaxOperators)
                    {
                        throw new InvalidDataException(
                            $"Plan exceeds the {PlanOperations.DefaultMaxOperators} operator complexity limit.");
                    }
                }

                if (!reader.IsEmptyElement)
                    ancestry.Push((reader.LocalName, reader.NamespaceURI));
            }

            return new PlanXmlPreflightResult(
                statements,
                operators,
                elements,
                attributes,
                sha256.Hash?.ToArray()
                    ?? throw new InvalidDataException("Could not hash the plan XML during preflight."));
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException($"Could not parse plan XML: {exception.Message}", exception);
        }
        finally
        {
            stream.Position = 0;
        }

    }
}
