using System.Collections.Concurrent;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

/// <summary>
/// Thread-safe in-memory implementation of the shared plan catalog.
/// </summary>
public class InMemoryPlanCatalog : IPlanCatalog
{
    private readonly ConcurrentDictionary<string, PlanSession> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    public void Register(PlanSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _sessions[session.SessionId] = session;
    }

    public bool TryRegister(PlanSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.SessionId);
        return _sessions.TryAdd(session.SessionId, session);
    }

    public bool Unregister(string sessionId) =>
        _sessions.TryRemove(sessionId, out _);

    public PlanSession? GetSession(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var session) ? session : null;

    public IReadOnlyList<PlanSessionSummary> GetAllSessions() =>
        _sessions.Values
            .OrderBy(session => session.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(session => session.SessionId, StringComparer.OrdinalIgnoreCase)
            .Select(session => new PlanSessionSummary
            {
                SessionId = session.SessionId,
                Label = session.Label,
                Source = session.Source,
                StatementCount = session.StatementCount,
                WarningCount = session.WarningCount,
                CriticalWarningCount = session.CriticalWarningCount,
                MissingIndexCount = session.MissingIndexCount,
                HasActualStats = session.HasActualStats
            })
            .ToList();
}
