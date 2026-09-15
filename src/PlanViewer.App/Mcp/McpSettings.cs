using System.Text.Json.Nodes;
using PlanViewer.App.Services;

namespace PlanViewer.App.Mcp;

internal sealed class McpSettings
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 5152;

    /// <summary>
    /// Reads through <see cref="SettingsFile"/> rather than opening settings.json directly.
    ///
    /// <para>These two keys live in that file alongside the proxy settings, and Settings &gt;
    /// Integrations writes them back through <see cref="SettingsFile"/>. Recomputing the path
    /// here meant this reader silently ignored <see cref="SettingsFile.RedirectForTestHost"/> —
    /// so the test host read the developer's real file while writing a temp one, which is worse
    /// than either on its own. One path, one redirect, both directions.</para>
    /// </summary>
    public static McpSettings Load()
    {
        var obj = SettingsFile.Read();
        var settings = new McpSettings();

        // Read each key on its own, so a malformed or wrongly typed one costs only itself
        // rather than taking the other back to its default with it.
        if (obj["mcp_enabled"] is JsonValue e && e.TryGetValue<bool>(out var enabled))
            settings.Enabled = enabled;
        if (obj["mcp_port"] is JsonValue p && p.TryGetValue<int>(out var port))
            settings.Port = port;

        return settings;
    }
}
