using System;
using System.CommandLine;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.Cli.Commands;

/// <summary>
/// Shared plan-analysis pipeline and per-result file output for the CLI commands.
/// Previously the parse -> analyze -> score sequence and the json/text/both file
/// writing were duplicated across analyze (offline + live) and querystore.
/// </summary>
public static class PlanAnalysisRunner
{
    /// <summary>
    /// Parses plan XML and runs the analysis + benefit-scoring pipeline. Pass
    /// serverMetadata for live captures (enables server-context rules); pass null
    /// for offline .sqlplan files.
    /// </summary>
    public static ParsedPlan Analyze(string planXml, AnalyzerConfig config, ServerMetadata? serverMetadata = null) =>
        PlanAnalysisPipeline.Analyze(planXml, config, serverMetadata);

    /// <summary>
    /// The message to show when the plan XML could not be parsed into a plan with at least one
    /// statement, or null when it could. The pipeline skips analysis for such a plan, and whatever
    /// parsed before a failure is partial, so writing it out would look like a clean result with no
    /// findings. A plan nested deeper than MaxParseDepth is refused this way (#589). So is XML that
    /// parses but holds no statement, such as a file that is not a showplan: it used to be caught
    /// only by the single-file path, and the live and Query Store paths wrote an empty result for it.
    /// </summary>
    public static string? ParseFailure(ParsedPlan plan) =>
        !string.IsNullOrWhiteSpace(plan.ParseError)
            ? $"Could not parse the plan XML: {plan.ParseError}"
            : PlanStatements.NoStatementsMessage(plan);

    /// <summary>
    /// Serializes an analysis result, and says what went wrong when the operator tree is too
    /// deep for AnalysisJson.MaxDepth, instead of the serializer's "possible object cycle" (#589).
    /// </summary>
    public static string SerializeResult(AnalysisResult result, JsonSerializerOptions jsonOptions)
    {
        try
        {
            return JsonSerializer.Serialize(result, jsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"{AnalysisJson.TooDeepMessage} Use --output text, or --warnings-only to leave out the operator tree.",
                exception);
        }
    }

    /// <summary>The values --output accepts.</summary>
    public static readonly IReadOnlyList<string> OutputFormats = new[] { "json", "text", "both" };

    /// <summary>
    /// Builds the --output option for a command, so every command that takes it accepts the same
    /// values and refuses anything else while the command line is parsed, before the command does
    /// any work. An unknown value used to get through: the query-store command and "analyze
    /// --server" wrote no files for it and exited 0, and "analyze &lt;file&gt;" printed json.
    ///
    /// <para>Any letter case is accepted, as --order-by accepts it: "-o JSON" printed json for a
    /// single file before, and refusing it now would break a command line that worked. Read the
    /// value with <see cref="ReadOutputFormat"/>, which lowercases it.</para>
    /// </summary>
    public static Option<string> CreateOutputOption(string description, string defaultFormat)
    {
        var option = new Option<string>("--output", "-o")
        {
            Description = description,
            DefaultValueFactory = _ => defaultFormat
        };
        // Help shows <both|json|text> from these, and shell completion offers them.
        option.CompletionSources.Add(OutputFormats.ToArray());
        option.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<string>();
            if (value is not null && !OutputFormats.Contains(value.ToLowerInvariant()))
            {
                result.AddError(
                    $"Argument '{value}' not recognized for --output. Must be one of: {string.Join(", ", OutputFormats)}");
            }
        });
        return option;
    }

    /// <summary>
    /// The --output value in lowercase, the form the commands and <see cref="WriteResultFilesAsync"/>
    /// compare against.
    /// </summary>
    public static string ReadOutputFormat(ParseResult parseResult, Option<string> option, string defaultFormat) =>
        (parseResult.GetValue(option) ?? defaultFormat).ToLowerInvariant();

    /// <summary>
    /// Writes {label}.analysis.json and/or {label}.analysis.txt into outDir per
    /// outputFormat ("json", "text", or "both"), honoring warningsOnly (which
    /// drops operator trees from the serialized output). Refuses any other format
    /// before it writes anything or changes the result, so a caller that did not go
    /// through <see cref="CreateOutputOption"/> cannot get an empty run that looks fine.
    /// </summary>
    public static async Task WriteResultFilesAsync(
        AnalysisResult result, string outDir, string label,
        string outputFormat, JsonSerializerOptions jsonOptions, bool warningsOnly)
    {
        if (!OutputFormats.Contains(outputFormat))
        {
            throw new ArgumentException(
                $"Unknown output format '{outputFormat}'. Use one of: {string.Join(", ", OutputFormats)}.",
                nameof(outputFormat));
        }

        if (warningsOnly)
        {
            foreach (var stmt in result.Statements)
                stmt.OperatorTree = null;
        }

        if (outputFormat == "json" || outputFormat == "both")
        {
            var json = SerializeResult(result, jsonOptions);
            await File.WriteAllTextAsync(Path.Combine(outDir, $"{label}.analysis.json"), json);
        }

        if (outputFormat == "text" || outputFormat == "both")
        {
            var txtPath = Path.Combine(outDir, $"{label}.analysis.txt");
            using var writer = new StreamWriter(txtPath);
            TextFormatter.WriteText(result, writer);
        }
    }
}
