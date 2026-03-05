using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using PlanViewer.Core.Models;

namespace PlanViewer.App.Mcp;

/// <summary>
/// Thread-safe bridge between UI plan state and MCP tools.
/// The UI registers/unregisters plans as tabs are opened/closed.
/// MCP tools read plan data without touching the UI thread.
/// </summary>
public sealed class PlanSessionManager
{
    public static PlanSessionManager Instance { get; } = new();

    private readonly ConcurrentDictionary<string, PlanSession> _sessions = new();

    public void Register(string sessionId, PlanSession session) =>
        _sessions[sessionId] = session;

    public void Unregister(string sessionId) =>
        _sessions.TryRemove(sessionId, out _);

    public PlanSession? GetSession(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var session) ? session : null;

    public IReadOnlyList<PlanSessionSummary> GetAllSessions() =>
        _sessions.Values.Select(s => new PlanSessionSummary
        {
            SessionId = s.SessionId,
            Label = s.Label,
            Source = s.Source,
            StatementCount = s.StatementCount,
            WarningCount = s.WarningCount,
            CriticalWarningCount = s.CriticalWarningCount,
            MissingIndexCount = s.MissingIndexCount,
            HasActualStats = s.HasActualStats
        }).ToList();
}

/// <summary>
/// Immutable snapshot of a loaded plan, safe for cross-thread reads by MCP tools.
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
