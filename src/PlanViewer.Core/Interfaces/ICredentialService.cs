namespace PlanViewer.Core.Interfaces;

public interface ICredentialService
{
    bool SaveCredential(string serverId, string username, string password);
    (string Username, string Password)? GetCredential(string serverId);
    bool DeleteCredential(string serverId);
    bool CredentialExists(string serverId);
    bool UpdateCredential(string serverId, string username, string password);
}
