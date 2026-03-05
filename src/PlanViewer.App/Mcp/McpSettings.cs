using System;
using System.IO;
using System.Text.Json;

namespace PlanViewer.App.Mcp;

internal sealed class McpSettings
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 5152;

    public static McpSettings Load()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".planview", "settings.json");

        if (!File.Exists(path))
            return new McpSettings();

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new McpSettings
            {
                Enabled = root.TryGetProperty("mcp_enabled", out var e) && e.GetBoolean(),
                Port = root.TryGetProperty("mcp_port", out var p) ? p.GetInt32() : 5152
            };
        }
        catch
        {
            return new McpSettings();
        }
    }
}
