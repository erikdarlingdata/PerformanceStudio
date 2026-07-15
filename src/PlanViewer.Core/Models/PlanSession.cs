namespace PlanViewer.Core.Models;

/// <summary>
/// Immutable snapshot of a loaded plan that can be shared by CLI, GUI, and MCP surfaces.
/// </summary>
public sealed class PlanSession
{
    public required string SessionId { get; init; }
    public required string Label { get; init; }
    public required string Source { get; init; }
    public required ParsedPlan Plan { get; init; }
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
    public string SessionId { get; set; } = "";
    public string Label { get; set; } = "";
    public string Source { get; set; } = "";
    public int StatementCount { get; set; }
    public int WarningCount { get; set; }
    public int CriticalWarningCount { get; set; }
    public int MissingIndexCount { get; set; }
    public bool HasActualStats { get; set; }
}
