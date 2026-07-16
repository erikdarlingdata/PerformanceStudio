using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

public static class PlanAnalysisPipeline
{
    public static ParsedPlan Analyze(
        string planXml,
        AnalyzerConfig config,
        CancellationToken cancellationToken = default) =>
        Analyze(planXml, config, serverMetadata: null, cancellationToken);

    public static ParsedPlan Analyze(
        string planXml,
        AnalyzerConfig config,
        ServerMetadata? serverMetadata,
        CancellationToken cancellationToken = default) =>
        Analyze(planXml, config, serverMetadata, cancellationToken, beforeAnalysis: null);

    internal static ParsedPlan Analyze(
        string planXml,
        AnalyzerConfig config,
        ServerMetadata? serverMetadata,
        CancellationToken cancellationToken,
        Action<ParsedPlan, CancellationToken>? beforeAnalysis)
    {
        ArgumentNullException.ThrowIfNull(planXml);
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();

        var plan = ShowPlanParser.Parse(planXml);
        cancellationToken.ThrowIfCancellationRequested();
        return AnalyzeParsed(plan, config, serverMetadata, cancellationToken, beforeAnalysis);
    }

    internal static async Task<ParsedPlan> AnalyzeAsync(
        string planXml,
        AnalyzerConfig config,
        ServerMetadata? serverMetadata,
        CancellationToken cancellationToken,
        Action<ParsedPlan, CancellationToken>? beforeAnalysis)
    {
        ArgumentNullException.ThrowIfNull(planXml);
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();

        var plan = await ShowPlanParser.ParseAsync(planXml, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return AnalyzeParsed(plan, config, serverMetadata, cancellationToken, beforeAnalysis);
    }

    public static ParsedPlan AnalyzeParsed(
        ParsedPlan plan,
        AnalyzerConfig config,
        ServerMetadata? serverMetadata = null,
        CancellationToken cancellationToken = default) =>
        AnalyzeParsed(plan, config, serverMetadata, cancellationToken, beforeAnalysis: null);

    internal static ParsedPlan AnalyzeParsed(
        ParsedPlan plan,
        AnalyzerConfig config,
        ServerMetadata? serverMetadata,
        CancellationToken cancellationToken,
        Action<ParsedPlan, CancellationToken>? beforeAnalysis)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(plan.ParseError) ||
            !plan.Batches.SelectMany(batch => batch.Statements).Any())
        {
            return plan;
        }

        beforeAnalysis?.Invoke(plan, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        PlanAnalyzer.AnalyzeCancellable(plan, config, serverMetadata, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        BenefitScorer.ScoreCancellable(plan, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return plan;
    }
}
