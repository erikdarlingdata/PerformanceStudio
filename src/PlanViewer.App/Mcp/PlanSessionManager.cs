using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;

namespace PlanViewer.App.Mcp;

/// <summary>
/// Application catalog shared by the desktop UI and MCP tools.
/// </summary>
public sealed class PlanSessionManager : IPlanCatalog
{
    public static PlanSessionManager Instance { get; } = new();

    private readonly ConcurrentDictionary<string, PlanSession> _sessions = new();

    public void Register(PlanSession session) =>
        _sessions[session.SessionId] = session;

    public bool TryRegister(PlanSession session) =>
        _sessions.TryAdd(session.SessionId, session);

    public bool Unregister(string sessionId) =>
        _sessions.TryRemove(sessionId, out _);

    public PlanSession? GetSession(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var session) ? session : null;

    public IReadOnlyList<PlanSessionSummary> GetAllSessions() =>
        _sessions.Values.Select(session => new PlanSessionSummary
        {
            SessionId = session.SessionId,
            Label = session.Label,
            Source = session.Source,
            StatementCount = session.StatementCount,
            WarningCount = session.WarningCount,
            CriticalWarningCount = session.CriticalWarningCount,
            MissingIndexCount = session.MissingIndexCount,
            HasActualStats = session.HasActualStats
        }).ToList();
}
