using System;
using PlanViewer.Core.Services;

namespace PlanViewer.App.Services;

internal enum ProxyMode
{
    System,
    Manual
}

internal sealed class ProxySettings
{
    private const string CredentialServerId = "__proxy__";

    public ProxyMode Mode { get; set; } = ProxyMode.System;
    public string Address { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>
    /// When false, <see cref="Save"/> writes the JSON fields but leaves the stored
    /// credential untouched. Used by the UI to support "leave blank to keep" — the
    /// user shouldn't have to retype the password every time they change a non-secret
    /// field like the proxy address.
    /// </summary>
    public bool TouchCredential { get; set; } = true;

    public static ProxySettings Load()
    {
        var obj = SettingsFile.Read();
        var s = new ProxySettings();

        if (obj["proxy_mode"]?.GetValue<string>() is { } modeStr &&
            Enum.TryParse<ProxyMode>(modeStr, ignoreCase: true, out var mode))
        {
            s.Mode = mode;
        }
        s.Address = obj["proxy_address"]?.GetValue<string>() ?? "";
        s.Username = obj["proxy_username"]?.GetValue<string>() ?? "";

        try
        {
            var cred = CredentialServiceFactory.Create().GetCredential(CredentialServerId);
            if (cred.HasValue)
                s.Password = cred.Value.Password;
        }
        catch
        {
            // Credential store unavailable — leave password empty.
        }

        return s;
    }

    public void Save()
    {
        SettingsFile.Update(o =>
        {
            o["proxy_mode"] = Mode.ToString().ToLowerInvariant();
            o["proxy_address"] = Address;
            o["proxy_username"] = Username;
        });

        if (!TouchCredential)
            return;

        try
        {
            var svc = CredentialServiceFactory.Create();
            if (string.IsNullOrEmpty(Password))
                svc.DeleteCredential(CredentialServerId);
            else
                svc.SaveCredential(CredentialServerId, Username, Password);
        }
        catch
        {
            // Credential store unavailable — password not persisted.
        }
    }
}
