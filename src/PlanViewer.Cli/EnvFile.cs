namespace PlanViewer.Cli;

/// <summary>The connection settings that a command line gave, or those after the .env file filled the gaps.</summary>
public sealed record ConnectionSettings(string? Server, string? Database, string? Login, bool TrustCert);

/// <summary>
/// Connection settings from the .env file in the working directory. Command-line options win over
/// the file, so <see cref="Fill"/> asks the file only for what the command line left out, and the
/// file records each key it supplied. A .env file can pick the server and turn off certificate
/// validation, so <see cref="Notice"/> names the settings it supplied instead of applying them
/// without a word.
/// </summary>
public sealed class EnvFile
{
    private readonly Dictionary<string, string> _values;
    private readonly List<string> _used = [];
    private bool _trustCertIgnored;

    public EnvFile(string? filePath, Dictionary<string, string> values)
    {
        FilePath = filePath;
        _values = values;

        /* A value with a control character could move the cursor and erase the notice, and no real
           setting needs one. Tab is allowed because it cannot reach another line. */
        var badKey = values.FirstOrDefault(kv =>
            kv.Key.StartsWith("PLANVIEW_", StringComparison.OrdinalIgnoreCase) &&
            kv.Value.Any(c => char.IsControl(c) && c != '\t')).Key;
        if (badKey != null)
            Error = $"The value of {Printable(badKey)} in {Printable(filePath ?? ".env")} has a control character. Fix the value or delete the file.";
    }

    /// <summary>The full path of the file, or null when the directory has no .env file.</summary>
    public string? FilePath { get; }

    /// <summary>A line for stderr when the file must not be used, or null. The command stops.</summary>
    public string? Error { get; }

    /// <summary>
    /// Fills the settings that the command line left out. With no server there is nothing to
    /// connect to, so the other settings would have no effect and the file is not asked for them.
    /// </summary>
    public ConnectionSettings Fill(ConnectionSettings commandLine)
    {
        var server = commandLine.Server ?? Use("PLANVIEW_SERVER");
        if (server is null)
            return commandLine;

        return new ConnectionSettings(
            server,
            commandLine.Database ?? Use("PLANVIEW_DATABASE"),
            commandLine.Login ?? Use("PLANVIEW_LOGIN"),
            commandLine.TrustCert || TrustCertFromFile(commandLine.Server is null));
    }

    /* The file may turn off certificate validation only for a server that the file itself named. A
       server from the command line was chosen by the user, and a file that sits in the working
       directory must not weaken the check for it. --trust-cert on the command line still works. */
    private bool TrustCertFromFile(bool serverFromFile)
    {
        if (serverFromFile)
            return UseFlag("PLANVIEW_TRUST_CERT");

        if (IsTrue("PLANVIEW_TRUST_CERT"))
            _trustCertIgnored = true;
        return false;
    }

    /// <summary>
    /// The file's password for a SQL login. Without a login the commands use the credential store or
    /// Windows authentication and ignore any password, so the file is not asked for one.
    /// </summary>
    public string? PasswordFor(ConnectionSettings settings) =>
        settings.Server is not null && !string.IsNullOrEmpty(settings.Login) ? Use("PLANVIEW_PASSWORD") : null;

    /// <summary>
    /// One line for stderr that names the file and the keys it supplied, or null when it supplied
    /// none. It never includes a value, so a password in the file stays out of the output.
    /// </summary>
    public string? Notice =>
        _used.Count == 0 ? null : $"Using settings from {Printable(FilePath ?? ".env")}: {string.Join(", ", _used)}";

    /// <summary>
    /// Lines for stderr that the user should see, in the order to print them. Ask after
    /// <see cref="PasswordFor"/>, because the password warning depends on the file having supplied one.
    /// </summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            var warnings = new List<string>();
            if (_trustCertIgnored)
                warnings.Add($"Ignored PLANVIEW_TRUST_CERT in {Printable(FilePath ?? ".env")}: the file can turn off certificate validation only for a server that the file names. Pass --trust-cert to turn it off for this server.");

            if (FilePath is not null && !OperatingSystem.IsWindows() &&
                _used.Contains("PLANVIEW_PASSWORD", StringComparer.OrdinalIgnoreCase) &&
                PermissionWarning(FilePath, File.GetUnixFileMode(FilePath)) is { } permission)
                warnings.Add(permission);
            return warnings;
        }
    }

    /// <summary>
    /// A warning when the group or other users can read or write a file that holds a password, as ssh
    /// does for a key file. Returns null when only the owner has access. It never includes a value.
    /// </summary>
    public static string? PermissionWarning(string filePath, UnixFileMode mode)
    {
        const UnixFileMode others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite;
        if ((mode & others) == 0)
            return null;

        var path = Printable(filePath);
        return $"Warning: {path} holds a password and other users can access it. Run: chmod 600 {path}";
    }

    private string? Use(string key)
    {
        if (!_values.TryGetValue(key, out var value))
            return null;

        Record(key);
        return value;
    }

    private bool IsTrue(string key) =>
        _values.TryGetValue(key, out var value) && value.Equals("true", StringComparison.OrdinalIgnoreCase);

    /* Only "true" changes the setting, so only then is the key recorded. */
    private bool UseFlag(string key)
    {
        if (!IsTrue(key))
            return false;

        Record(key);
        return true;
    }

    private void Record(string key)
    {
        if (!_used.Contains(key, StringComparer.OrdinalIgnoreCase))
            _used.Add(key);
    }

    /* A path or key from the file goes to the terminal, so a control character in it is shown as '?'. */
    private static string Printable(string text) =>
        string.Concat(text.Select(c => char.IsControl(c) ? '?' : c));
}
