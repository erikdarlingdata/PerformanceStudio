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
    public static ParsedPlan Analyze(string planXml, AnalyzerConfig config, ServerMetadata? serverMetadata = null)
    {
        var plan = ShowPlanParser.Parse(planXml);
        if (serverMetadata != null)
            PlanAnalyzer.Analyze(plan, config, serverMetadata);
        else
            PlanAnalyzer.Analyze(plan, config);
        BenefitScorer.Score(plan);
        return plan;
    }

    /// <summary>
    /// Writes {label}.analysis.json and/or {label}.analysis.txt into outDir per
    /// outputFormat ("json", "text", or "both"), honoring warningsOnly (which
    /// drops operator trees from the serialized output).
    /// </summary>
    public static async Task WriteResultFilesAsync(
        AnalysisResult result, string outDir, string label,
        string outputFormat, JsonSerializerOptions jsonOptions, bool warningsOnly)
    {
        if (warningsOnly)
        {
            foreach (var stmt in result.Statements)
                stmt.OperatorTree = null;
        }

        if (outputFormat == "json" || outputFormat == "both")
        {
            var json = JsonSerializer.Serialize(result, jsonOptions);
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
