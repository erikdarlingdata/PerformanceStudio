using System;
using System.Collections.Generic;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.App.Mcp;

/// <summary>
/// Application singleton backed by the same catalog abstraction used by Repl.
/// </summary>
public sealed class PlanSessionManager : IPlanCatalog
{
    public static PlanSessionManager Instance { get; } = new();

    private readonly InMemoryPlanCatalog _catalog = new();

    public void Register(PlanSession session) => _catalog.Register(session);

    public bool TryRegister(PlanSession session) => _catalog.TryRegister(session);

    // Compatibility overload for existing UI and Query Store callers during the pilot.
    public void Register(string sessionId, PlanSession session)
    {
        if (!sessionId.Equals(session.SessionId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The registration key must match the session ID.", nameof(sessionId));
        _catalog.Register(session);
    }

    public bool Unregister(string sessionId) => _catalog.Unregister(sessionId);

    public PlanSession? GetSession(string sessionId) => _catalog.GetSession(sessionId);

    public IReadOnlyList<PlanSessionSummary> GetAllSessions() => _catalog.GetAllSessions();
}
