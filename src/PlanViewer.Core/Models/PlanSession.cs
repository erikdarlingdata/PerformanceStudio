using PlanViewer.Core.Output;

namespace PlanViewer.Core.Models;

/// <summary>
/// Container for a loaded plan and its detached analysis projection. The references are
/// init-only; the historical nested plan and output models remain mutable.
/// </summary>
public sealed class PlanSession
{
    public required string SessionId { get; init; }
    public required string Label { get; init; }
    public required string Source { get; init; }
    public required ParsedPlan Plan { get; init; }
    public AnalysisResult? Analysis { get; init; }
    public string? QueryText { get; init; }
    public string? ConnectionInfo { get; init; }
    public int StatementCount { get; init; }
    public bool HasActualStats { get; init; }
    public int WarningCount { get; init; }
    public int CriticalWarningCount { get; init; }
    public int MissingIndexCount { get; init; }
}

public sealed class PlanSessionSummary
{
    public string SessionId { get; init; } = "";
    public string Label { get; init; } = "";
    public string Source { get; init; } = "";
    public int StatementCount { get; init; }
    public int WarningCount { get; init; }
    public int CriticalWarningCount { get; init; }
    public int MissingIndexCount { get; init; }
    public bool HasActualStats { get; init; }
}
