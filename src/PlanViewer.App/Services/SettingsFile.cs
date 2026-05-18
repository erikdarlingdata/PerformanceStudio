using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlanViewer.App.Services;

internal static class SettingsFile
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".planview", "settings.json");

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
