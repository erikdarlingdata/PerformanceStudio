using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PlanViewer.Core.Models;

namespace PlanViewer.App.Services;

public class ConnectionStore
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".planview");

    private static readonly string ConfigFile = Path.Combine(ConfigDir, "connections.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public List<ServerConnection> Load()
    {
        if (!File.Exists(ConfigFile))
            return new List<ServerConnection>();

        try
        {
            var json = File.ReadAllText(ConfigFile);
            return JsonSerializer.Deserialize<List<ServerConnection>>(json) ?? new List<ServerConnection>();
        }
        catch
        {
            return new List<ServerConnection>();
        }
    }

    public void Save(List<ServerConnection> connections)
    {
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
