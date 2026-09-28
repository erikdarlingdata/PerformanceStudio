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
    internal static void RedirectForTestHost(string directory)
    {
        Path = System.IO.Path.Combine(directory, "settings.json");
    }

    public static JsonObject Read() => Read(out _);

    /// <param name="unreadable">True when <see cref="Path"/> exists but could not even be read
    /// (locked, permissions) — see <see cref="SettingsFileStore"/> for why that blocks a write.</param>
    private static JsonObject Read(out bool unreadable)
    {
        var outcome = SettingsFileStore.Read<JsonObject>(
            Path,
            nameof(SettingsFile),
            // A JsonArray (or any other non-object JSON value) casts to null here rather than
            // throwing, which is exactly the "valid JSON, wrong shape" case the shared helper
            // treats the same as a parse failure — see SettingsFileStore's doc comment.
            json => JsonNode.Parse(json) as JsonObject,
            out var parsed);

        unreadable = outcome == SettingsFileStore.ReadOutcome.Unreadable;

        return parsed ?? new JsonObject();
    }

    /// <summary>
    /// Reads, mutates, and writes back. Throws <see cref="IOException"/>, rather than
    /// overwriting the file, if the read here finds <see cref="Path"/> unreadable — callers
    /// (Settings &gt; Integrations) must catch it and tell the user, not let it crash the app.
    /// Only this call's own read decides, so a read elsewhere can't let it write over the file,
    /// and every Update is itself a retry.
    /// </summary>
    public static void Update(Action<JsonObject> mutate)
    {
        var obj = Read(out var unreadable);
        if (unreadable)
            throw SettingsFileStore.UnreadableSaveRefused(Path);

        mutate(obj);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var json = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteAllText(Path, json);
    }
}
