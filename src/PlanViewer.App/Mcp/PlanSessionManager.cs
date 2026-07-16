using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using CorePlanSession = PlanViewer.Core.Models.PlanSession;
using CorePlanSessionSummary = PlanViewer.Core.Models.PlanSessionSummary;

namespace PlanViewer.App.Mcp;

/// <summary>
/// Application singleton that preserves the historical App API while implementing the
/// shared Core catalog contract.
/// </summary>
public sealed class PlanSessionManager : IPlanCatalog
{
    public static PlanSessionManager Instance { get; } = new();

    private readonly ConcurrentDictionary<string, PlanSession> _sessions = new();

    public void Register(CorePlanSession session) =>
        _sessions[session.SessionId] = PlanSession.FromCore(session);

    public bool TryRegister(CorePlanSession session) =>
        _sessions.TryAdd(session.SessionId, PlanSession.FromCore(session));

    public void Register(string sessionId, PlanSession session) =>
        _sessions[sessionId] = session;

    public void Unregister(string sessionId) =>
        _sessions.TryRemove(sessionId, out _);

    public PlanSession? GetSession(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var session) ? session : null;

    public IReadOnlyList<PlanSessionSummary> GetAllSessions() =>
        _sessions.Values.Select(PlanSessionSummary.FromApp).ToList();

    bool IPlanCatalog.Unregister(string sessionId) => _sessions.TryRemove(sessionId, out _);

    CorePlanSession? IPlanCatalog.GetSession(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var session) ? session.ToCore() : null;

    IReadOnlyList<CorePlanSessionSummary> IPlanCatalog.GetAllSessions() =>
        _sessions.Values.Select(session => new CorePlanSessionSummary
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

/// <summary>
/// Historical App-facing plan-session model retained for source and binary compatibility.
/// </summary>
public sealed class PlanSession
{
    public required string SessionId { get; init; }
    public required string Label { get; init; }
    public required string Source { get; init; }
    public required ParsedPlan Plan { get; init; }
    public AnalysisResult? Analysis { get; init; }
    public string? RawPlanXml { get; init; }
    public string? DatabaseName { get; init; }
    public string? QueryText { get; init; }
    public string? ConnectionInfo { get; init; }
    public int StatementCount { get; init; }
    public bool HasActualStats { get; init; }
    public int WarningCount { get; init; }
    public int CriticalWarningCount { get; init; }
    public int MissingIndexCount { get; init; }

    internal CorePlanSession ToCore(string? sessionId = null) => new()
    {
        SessionId = sessionId ?? SessionId,
        Label = Label,
        Source = Source,
        Plan = Plan,
        Analysis = Analysis,
        RawPlanXml = RawPlanXml,
        DatabaseName = DatabaseName,
        QueryText = QueryText,
        ConnectionInfo = ConnectionInfo,
        StatementCount = StatementCount,
        HasActualStats = HasActualStats,
        WarningCount = WarningCount,
        CriticalWarningCount = CriticalWarningCount,
        MissingIndexCount = MissingIndexCount
    };

    internal static PlanSession FromCore(CorePlanSession session) => new()
    {
        SessionId = session.SessionId,
        Label = session.Label,
        Source = session.Source,
        Plan = session.Plan,
        Analysis = session.Analysis,
        RawPlanXml = session.RawPlanXml,
        DatabaseName = session.DatabaseName,
        QueryText = session.QueryText,
        ConnectionInfo = session.ConnectionInfo,
        StatementCount = session.StatementCount,
        HasActualStats = session.HasActualStats,
        WarningCount = session.WarningCount,
        CriticalWarningCount = session.CriticalWarningCount,
        MissingIndexCount = session.MissingIndexCount
    };

    public static implicit operator CorePlanSession(PlanSession session) => session.ToCore();
}

/// <summary>
/// Historical mutable App summary DTO retained for compatibility.
/// </summary>
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

    internal static PlanSessionSummary FromApp(PlanSession session) => new()
    {
        SessionId = session.SessionId,
        Label = session.Label,
        Source = session.Source,
        StatementCount = session.StatementCount,
        WarningCount = session.WarningCount,
        CriticalWarningCount = session.CriticalWarningCount,
        MissingIndexCount = session.MissingIndexCount,
        HasActualStats = session.HasActualStats
    };
}
