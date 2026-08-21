using System.Collections.Generic;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

/// <summary>
/// Gathers the warnings scattered across an operator tree into one list (#440).
///
/// <para>Lives in Core rather than next to the panel that renders it for two reasons: it is a plan
/// tree walk over Core's own models with nothing UI about it, and putting it here means it can be
/// tested without standing up Avalonia.</para>
/// </summary>
public static class WarningIndex
{
    /// <summary>
    /// Every warning hanging off an operator beneath <paramref name="root"/>, paired with the
    /// operator carrying it, in no particular order — callers sort for themselves, because the
    /// statement panel wants them by benefit while other callers may not.
    /// </summary>
    public static List<(PlanNode Node, PlanWarning Warning)> CollectOperatorWarnings(PlanNode? root)
    {
        var collected = new List<(PlanNode, PlanWarning)>();
        if (root == null)
            return collected;

        /* Explicit stack rather than recursion: a deep plan is exactly the case this feature exists
           for, and #430 was a crash caused by assuming operator trees are shallow. */
        var pending = new Stack<PlanNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            foreach (var warning in node.Warnings)
                collected.Add((node, warning));
            foreach (var child in node.Children)
                pending.Push(child);
        }

        return collected;
    }
}
