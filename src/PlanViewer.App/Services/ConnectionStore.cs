using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PlanViewer.Core.Models;

namespace PlanViewer.App.Services;

public class ConnectionStore
{
    // Not readonly only because RedirectForTestHost exists; nothing in the product assigns
    // these outside the static field initializers below (mirrors AppSettingsService).
    private static string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".planview");

    private static string ConfigFile = Path.Combine(ConfigDir, "connections.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Set by <see cref="Load"/> when <see cref="ConfigFile"/> exists but could not even be read
    /// (locked, permissions). <see cref="Save"/> refuses to write while this is set — see
    /// <see cref="SettingsFileStore"/> for why, and <see cref="AppSettingsService"/>'s matching
    /// field for how the block clears on a later successful read.
    /// </summary>
    private static bool _saveBlocked;

    /// <summary>
    /// Points this store at a throwaway directory for the test host, the same way
    /// <see cref="AppSettingsService.RedirectStorageForTestHost"/> does for appsettings.json.
    /// Without it, a test that exercised a save path would write the developer's own saved
    /// server list — this store's path used to be a plain static readonly field under the real
    /// per-user profile, with no such seam.
    /// </summary>
    internal static void RedirectForTestHost(string directory)
    {
        ConfigDir = directory;
        ConfigFile = Path.Combine(directory, "connections.json");
        _saveBlocked = false;
    }

    /// <summary>
    /// The file connections are read from and written to right now. Exposed so the test suite
    /// can read and write it directly (mirrors <see cref="AppSettingsService.SettingsFilePath"/>).
    /// </summary>
    internal static string ConfigFilePath => ConfigFile;

    public List<ServerConnection> Load()
    {
        var outcome = SettingsFileStore.Read<List<ServerConnection>>(
            ConfigFile,
            nameof(ConnectionStore),
            json => JsonSerializer.Deserialize<List<ServerConnection>>(json),
            out var parsed);

        _saveBlocked = outcome == SettingsFileStore.ReadOutcome.Unreadable;

        return parsed ?? new List<ServerConnection>();
    }

    /// <summary>
    /// Saves the connection list. Throws <see cref="IOException"/>, rather than overwriting the
    /// file, if the last <see cref="Load"/> found <see cref="ConfigFile"/> unreadable — callers
    /// (ConnectionDialog) must catch it and tell the user, not let it crash the app.
    /// </summary>
    public void Save(List<ServerConnection> connections)
    {
        if (_saveBlocked)
            throw SettingsFileStore.UnreadableSaveRefused(ConfigFile);

        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(connections, JsonOptions);
        AtomicFile.WriteAllText(ConfigFile, json);
    }

    public void AddOrUpdate(ServerConnection connection)
    {
        var connections = Load();
        var existing = connections.FirstOrDefault(c =>
            c.ServerName.Equals(connection.ServerName, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            existing.AuthenticationType = connection.AuthenticationType;
            existing.EncryptMode = connection.EncryptMode;
            existing.TrustServerCertificate = connection.TrustServerCertificate;
            existing.ApplicationIntentReadOnly = connection.ApplicationIntentReadOnly;
            existing.DisplayName = connection.DisplayName;
            existing.DatabaseName = connection.DatabaseName;
            existing.LastConnected = DateTime.Now;
        }
        else
        {
            connection.CreatedDate = DateTime.Now;
            connection.LastConnected = DateTime.Now;
            connections.Add(connection);
        }

        Save(connections);
    }
}
