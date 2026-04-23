using System;
using System.Collections.Generic;

namespace PlanViewer.Core.Services;

/// <summary>
/// Per-wait-type knowledge used when surfacing wait stats as warnings.
///
/// CONTENT STATUS: descriptions and fix text are intentionally empty. The prior
/// copy was AI-drafted without expert review and Joe Obbish flagged some of it
/// as misleading (#215 D3). Entries are kept so the rendering pipeline keeps
/// emitting warnings with names, benefit %, and effective latency, but without
/// speculative advice until Erik / Joe fill in content.
///
/// ShowEffectiveLatency flags stay because they're structural (emit a
/// wait_ms / wait_count statistic), not creative advice.
/// </summary>
public static class WaitStatsKnowledge
{
    public sealed class Entry
    {
        /// <summary>Short, human-readable explanation of what the wait represents.</summary>
        public string Description { get; init; } = "";

        /// <summary>Concrete guidance on how to reduce or eliminate the wait.</summary>
        public string HowToFix { get; init; } = "";

        /// <summary>
        /// If true, surface an effective per-wait latency (wait_ms / wait_count)
        /// in the warning message. Useful for latch/I/O waits where tail latency is
        /// the thing to focus on.
        /// </summary>
        public bool ShowEffectiveLatency { get; init; }
    }

    private static readonly Entry Default = new();

    // Structural flags only (effective-latency display). Description/HowToFix pending
    // expert-written content — see file-level comment.
    private static readonly Dictionary<string, Entry> Exact = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PAGEIOLATCH_SH"] = new() { ShowEffectiveLatency = true },
        ["PAGEIOLATCH_EX"] = new() { ShowEffectiveLatency = true },
        ["PAGEIOLATCH_UP"] = new() { ShowEffectiveLatency = true },
        ["PAGEIOLATCH_DT"] = new() { ShowEffectiveLatency = true },
    };

    /// <summary>
    /// Look up the knowledge entry for a wait type. Falls back through family prefixes
    /// for structural flags (effective-latency display) before returning a default.
    /// Never returns null.
    /// </summary>
    public static Entry Lookup(string waitType)
    {
        if (string.IsNullOrEmpty(waitType)) return Default;
        if (Exact.TryGetValue(waitType, out var exact)) return exact;

        var wt = waitType.ToUpperInvariant();

        if (wt.StartsWith("PAGEIOLATCH_"))
            return new Entry { ShowEffectiveLatency = true };

        return Default;
    }
}
