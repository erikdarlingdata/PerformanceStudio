using PlanViewer.Core.Models;

namespace PlanViewer.Core.Interfaces;

/// <summary>
/// Shared catalog of plans loaded by a long-lived CLI, GUI, test, or MCP session.
/// </summary>
public interface IPlanCatalog
{
    void Register(PlanSession session);
    bool TryRegister(PlanSession session);
    bool Unregister(string sessionId);
    PlanSession? GetSession(string sessionId);
    IReadOnlyList<PlanSessionSummary> GetAllSessions();
}
