using System.Xml;

namespace PlanViewer.Core.Services;

internal readonly record struct PlanXmlPreflightResult(int StatementCount, int OperatorCount);

internal static class PlanXmlPreflight
{
    internal const int DefaultMaxXmlDepth = 512;

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

        var statements = 0;
        var operators = 0;
        stream.Position = 0;
        try
        {
            using var reader = XmlReader.Create(stream, settings);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stream.Length > PlanOperations.DefaultMaxPlanFileBytes ||
                    stream.Position > PlanOperations.DefaultMaxPlanFileBytes)
                {
                    throw new InvalidDataException(
                        $"Plan file exceeds the {PlanOperations.DefaultMaxPlanFileBytes / (1024 * 1024)} MiB size limit.");
                }
                if (reader.NodeType != XmlNodeType.Element)
                    continue;
                if (reader.Depth > DefaultMaxXmlDepth)
                {
                    throw new InvalidDataException(
                        $"Plan XML exceeds the supported depth limit of {DefaultMaxXmlDepth}.");
                }

                switch (reader.LocalName)
                {
                    case "StmtSimple":
                    case "StmtCursor":
                        statements++;
                        if (statements > PlanOperations.DefaultMaxStatements)
                        {
                            throw new InvalidDataException(
                                $"Plan exceeds the {PlanOperations.DefaultMaxStatements} statement complexity limit.");
                        }
                        break;
                    case "RelOp":
                        operators++;
                        if (operators > PlanOperations.DefaultMaxOperators)
                        {
                            throw new InvalidDataException(
                                $"Plan exceeds the {PlanOperations.DefaultMaxOperators} operator complexity limit.");
                        }
                        break;
                }
            }
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException($"Could not parse plan XML: {exception.Message}", exception);
        }
        finally
        {
            stream.Position = 0;
        }

        return new PlanXmlPreflightResult(statements, operators);
    }
}
