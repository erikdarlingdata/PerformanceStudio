using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

internal static class PlanAnalysisPipeline
{
    internal static ParsedPlan Analyze(
        string planXml,
        AnalyzerConfig config,
        CancellationToken cancellationToken = default) =>
        Analyze(planXml, config, serverMetadata: null, cancellationToken);

    internal static ParsedPlan Analyze(
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
