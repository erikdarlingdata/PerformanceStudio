using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlanViewer.Core.Services;

/// <summary>
/// Loads and serves the wait stats configuration from Resources/WaitStats.json
/// (embedded in PlanViewer.Core). Per issue #215 this is the single source of
/// truth — no other file is allowed to duplicate per-wait classifications,
/// time-calculation routing, or display flags.
/// </summary>
public static class WaitStatsConfig
{
    public sealed class Entry
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = "";

        [JsonPropertyName("isPreemptive")]
        public bool IsPreemptive { get; init; }

        [JsonPropertyName("isExternal")]
        public bool IsExternal { get; init; }

        [JsonPropertyName("isImplemented")]
        public bool IsImplemented { get; init; }

        [JsonPropertyName("isEnabled")]
        public bool IsEnabled { get; init; }

        [JsonPropertyName("showWaitCount")]
        public bool? ShowWaitCount { get; init; }

        [JsonPropertyName("showAverageWaitTime")]
        public bool? ShowAverageWaitTime { get; init; }

        [JsonPropertyName("timeCalculationModel")]
        public string? TimeCalculationModel { get; init; }
    }

    private static readonly Lazy<Dictionary<string, Entry>> _byName = new(Load);

    public static Entry? Get(string waitType)
    {
        if (string.IsNullOrEmpty(waitType)) return null;
        return _byName.Value.TryGetValue(waitType, out var e) ? e : null;
    }

    /// <summary>
    /// True iff the wait's time calculation model is "cpu time based" (preemptive
    /// or external — the worker is CPU-busy in kernel rather than descheduled).
    /// Lookup misses return false, preserving the prior default behavior for
    /// unknown waits.
    /// </summary>
    public static bool IsExternal(string waitType)
        => Get(waitType)?.IsExternal ?? false;

    /// <summary>
    /// True iff effective per-wait latency (wait_ms / wait_count) should be
    /// surfaced alongside totals. Defaults to false when the wait isn't in the
    /// config — i.e. unknown waits don't get a latency line.
    /// </summary>
    public static bool ShowAverageWaitTime(string waitType)
        => Get(waitType)?.ShowAverageWaitTime ?? false;

    private static Dictionary<string, Entry> Load()
    {
        // The JSON ships embedded in PlanViewer.Core (manifest name
        // PlanViewer.Core.Resources.WaitStats.json) and is also embedded into
        // PlanViewer.Web's assembly via a linked <EmbeddedResource>, where the
        // manifest prefix is PlanViewer.Web.* — so resolve by suffix to handle both.
        var asm = typeof(WaitStatsConfig).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("Resources.WaitStats.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Embedded resource ending in 'Resources.WaitStats.json' not found in {asm.GetName().Name}. " +
                "Check that Resources/WaitStats.json is included as <EmbeddedResource> in the project.");

        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Failed to open embedded resource '{resourceName}'.");

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();

        var doc = JsonSerializer.Deserialize<Document>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) ?? throw new InvalidOperationException("WaitStats.json deserialized to null.");

        var dict = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in doc.WaitStats)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            dict[entry.Name] = entry;
        }
        return dict;
    }

    private sealed class Document
    {
        [JsonPropertyName("waitStats")]
        public List<Entry> WaitStats { get; init; } = new();
    }
}
