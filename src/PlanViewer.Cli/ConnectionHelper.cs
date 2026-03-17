using Microsoft.Data.SqlClient;

namespace PlanViewer.Cli;

public static class ConnectionHelper
{
    public static string BuildConnectionString(
        string server, string database, string login, string password,
        bool trustCert, bool multipleActiveResultSets = false)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            UserID = login,
            Password = password,
            ApplicationName = "PlanViewer",
            ConnectTimeout = 15,
            TrustServerCertificate = trustCert,
            Encrypt = trustCert ? SqlConnectionEncryptOption.Optional : SqlConnectionEncryptOption.Mandatory
        };

        if (multipleActiveResultSets)
            builder.MultipleActiveResultSets = true;

        return builder.ConnectionString;
    }

    public static Dictionary<string, string> LoadEnvFile()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");

        if (!File.Exists(envPath))
            return result;

        foreach (var line in File.ReadAllLines(envPath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            var eqIndex = trimmed.IndexOf('=');
            if (eqIndex <= 0)
                continue;

            var key = trimmed[..eqIndex].Trim();
            var value = trimmed[(eqIndex + 1)..].Trim();

            // Strip surrounding quotes
            if (value.Length >= 2 &&
                ((value.StartsWith('"') && value.EndsWith('"')) ||
                 (value.StartsWith('\'') && value.EndsWith('\''))))
            {
                value = value[1..^1];
            }

            result[key] = value;
        }

        return result;
    }
}
