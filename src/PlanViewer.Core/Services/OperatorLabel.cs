using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

/// <summary>
/// The one place that spells an operator label. The JSON output prints it in the Operator field
/// of a warning, and a suppression's "operator" text is matched against it (#651), so the two
/// share this method and can never drift.
/// </summary>
internal static class OperatorLabel
{
    /// <summary>
    /// Includes the object name for data access operators (scans, seeks, lookups) where it helps
    /// identify which table or index is involved.
    /// </summary>
    internal static string Format(PlanNode node)
    {
        if (!string.IsNullOrEmpty(node.ObjectName))
        {
            var objRef = !string.IsNullOrEmpty(node.DatabaseName)
                ? $"{node.DatabaseName}.{node.ObjectName}"
                : node.ObjectName;
            return $"{node.PhysicalOp} on {objRef} (Node {node.NodeId})";
        }

        return $"{node.PhysicalOp} (Node {node.NodeId})";
    }
}
