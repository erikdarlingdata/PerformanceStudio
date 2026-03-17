using System;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using PlanViewer.Core.Interfaces;

namespace PlanViewer.Core.Models;

public class ServerConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ServerName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string AuthenticationType { get; set; } = AuthenticationTypes.Windows;
    public string? Description { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public DateTime LastConnected { get; set; } = DateTime.Now;
    public bool IsFavorite { get; set; }
    public string EncryptMode { get; set; } = "Mandatory";
    public bool TrustServerCertificate { get; set; } = false;

    [JsonIgnore]
    public string AuthenticationDisplay => AuthenticationType switch
    {
        AuthenticationTypes.EntraMFA => "Microsoft Entra MFA",
        AuthenticationTypes.SqlServer => "SQL Server",
        _ => "Windows"
    };

    public string GetConnectionString(ICredentialService credentialService, string? databaseName = null)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = ServerName,
            ApplicationName = "PlanViewer",
            ConnectTimeout = 15,
            MultipleActiveResultSets = true,
            TrustServerCertificate = TrustServerCertificate,
            Encrypt = EncryptMode switch
            {
                "Optional" => SqlConnectionEncryptOption.Optional,
                "Strict" => SqlConnectionEncryptOption.Strict,
                _ => SqlConnectionEncryptOption.Mandatory
            }
        };

        if (!string.IsNullOrEmpty(databaseName))
            builder.InitialCatalog = databaseName;

        switch (AuthenticationType)
        {
            case AuthenticationTypes.EntraMFA:
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive;
                var mfaCred = credentialService.GetCredential(Id);
                if (mfaCred.HasValue && !string.IsNullOrEmpty(mfaCred.Value.Username))
                    builder.UserID = mfaCred.Value.Username;
                break;

            case AuthenticationTypes.SqlServer:
                var cred = credentialService.GetCredential(Id);
                if (!cred.HasValue)
                    throw new InvalidOperationException(
                        $"SQL Server authentication credentials are missing for server '{ServerName}'. Please configure credentials before connecting.");
                builder.UserID = cred.Value.Username;
                builder.Password = cred.Value.Password;
                break;

            default: // Windows
                builder.IntegratedSecurity = true;
                break;
        }

        return builder.ConnectionString;
    }

    public bool HasStoredCredentials(ICredentialService credentialService)
    {
        if (AuthenticationType is AuthenticationTypes.Windows or AuthenticationTypes.EntraMFA)
            return true;

        return credentialService.CredentialExists(Id);
    }
}
