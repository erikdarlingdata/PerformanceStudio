using System.Collections.Concurrent;
using PlanViewer.Core.Interfaces;

namespace PlanViewer.Core.Services;

/// <summary>
/// In-memory credential service for platforms without native credential storage (e.g. Linux).
/// Credentials are held for the lifetime of the app but not persisted to disk.
/// </summary>
public class InMemoryCredentialService : ICredentialService
{
    private readonly ConcurrentDictionary<string, (string Username, string Password)> _store = new();

    public bool SaveCredential(string serverId, string username, string password)
    {
        _store[serverId] = (username, password);
        return true;
    }

    public (string Username, string Password)? GetCredential(string serverId)
    {
        return _store.TryGetValue(serverId, out var cred) ? cred : null;
    }

    public bool DeleteCredential(string serverId)
    {
        return _store.TryRemove(serverId, out _);
    }

    public bool CredentialExists(string serverId)
    {
        return _store.ContainsKey(serverId);
    }

    public bool UpdateCredential(string serverId, string username, string password)
    {
        return SaveCredential(serverId, username, password);
    }
}
