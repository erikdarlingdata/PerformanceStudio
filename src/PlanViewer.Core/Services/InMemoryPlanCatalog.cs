using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

/// <summary>
/// Thread-safe in-memory implementation of the shared plan catalog.
/// </summary>
internal sealed class InMemoryPlanCatalog : IPlanCatalog
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, PlanSession> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    public void Register(PlanSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.SessionId);
        lock (_syncRoot)
            _sessions[session.SessionId] = session;
    }

    public bool TryRegister(PlanSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.SessionId);
        lock (_syncRoot)
            return _sessions.TryAdd(session.SessionId, session);
    }

    public bool Unregister(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_syncRoot)
            return _sessions.Remove(sessionId);
    }

    public PlanSession? GetSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_syncRoot)
            return _sessions.GetValueOrDefault(sessionId);
    }

    public IReadOnlyList<PlanSessionSummary> GetAllSessions()
    {
        lock (_syncRoot)
        {
            return _sessions.Values
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
    }
}
