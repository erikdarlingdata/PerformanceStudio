using System.Runtime.Versioning;
using Meziantou.Framework.Win32;
using PlanViewer.Core.Interfaces;

namespace PlanViewer.Core.Services;

[SupportedOSPlatform("windows5.1.2600")]
public class WindowsCredentialService : ICredentialService
{
    private const string Prefix = "planview:";

    public bool SaveCredential(string serverId, string username, string password)
    {
        try
        {
            CredentialManager.WriteCredential(
                applicationName: Prefix + serverId,
                userName: username,
                secret: password,
                comment: "planview credential",
                persistence: CredentialPersistence.Enterprise);
            return true;
        }
        catch { return false; }
    }

    public (string Username, string Password)? GetCredential(string serverId)
    {
        var cred = CredentialManager.ReadCredential(Prefix + serverId);
        if (cred == null) return null;
        return (cred.UserName ?? "", cred.Password ?? "");
    }

    public bool DeleteCredential(string serverId)
    {
        try
        {
            CredentialManager.DeleteCredential(Prefix + serverId);
            return true;
        }
        catch { return false; }
    }

    public bool CredentialExists(string serverId)
    {
        return CredentialManager.ReadCredential(Prefix + serverId) != null;
    }

    public bool UpdateCredential(string serverId, string username, string password)
    {
        return SaveCredential(serverId, username, password);
    }

    /// <summary>
    /// Enumerates all planview credentials in the Windows Credential Manager.
    /// </summary>
    public IReadOnlyList<(string ServerName, string Username)> ListAll()
    {
        return CredentialManager.EnumerateCredentials()
            .Where(c => c.ApplicationName.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(c => (c.ApplicationName[Prefix.Length..], c.UserName ?? ""))
            .ToList();
    }
}
