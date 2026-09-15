using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlanViewer.App.Services;

internal static class SettingsFile
{
    public static string Path { get; private set; } = DefaultPath();

    private static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".planview", "settings.json");

    /// <summary>
    /// Points this store at a throwaway directory for the test host, the same way
    /// <see cref="AppSettingsService.RedirectStorageForTestHost"/> does for appsettings.json.
    ///
    /// <para>This file holds the MCP port and the proxy configuration, which Settings &gt;
    /// Integrations writes. Without the redirect a test that exercised that save path would
    /// rewrite the developer's own settings.json — the failure mode that made the sibling
    /// redirect necessary in the first place. When this is never called the path keeps its
    /// default, so the real app is untouched.</para>
    /// </summary>
    internal static void RedirectForTestHost(string directory) =>
        Path = System.IO.Path.Combine(directory, "settings.json");

    public static JsonObject Read()
    {
        if (!File.Exists(Path))
            return new JsonObject();

        try
        {
            var json = File.ReadAllText(Path);
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    public static void Update(Action<JsonObject> mutate)
    {
        var obj = Read();
        mutate(obj);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var json = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteAllText(Path, json);
    }
}
