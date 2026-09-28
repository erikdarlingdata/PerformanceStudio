namespace PlanViewer.Cli;

/// <summary>
/// Connection settings from the .env file in the working directory. Command-line options win over
/// the file, so a command asks for a value only when its option was left out, and the file records
/// each key it supplied. A .env file can pick the server and turn off certificate validation, so
/// <see cref="Notice"/> names the settings it supplied instead of applying them without a word.
/// </summary>
public sealed class EnvFile
{
    private readonly Dictionary<string, string> _values;
    private readonly List<string> _used = [];

    public EnvFile(string? filePath, Dictionary<string, string> values)
    {
        FilePath = filePath;
        _values = values;
    }

    /// <summary>The full path of the file, or null when the directory has no .env file.</summary>
    public string? FilePath { get; }

    /// <summary>Returns the file's value for <paramref name="key"/>, or null, and records a hit.</summary>
    public string? Use(string key)
    {
        if (!_values.TryGetValue(key, out var value))
            return null;

        Record(key);
        return value;
    }

    /// <summary>
    /// True when the file sets <paramref name="key"/> to "true". Only then is the key recorded,
    /// because any other value leaves the setting as it was.
    /// </summary>
    public bool UseFlag(string key)
    {
        if (!_values.TryGetValue(key, out var value) || !value.Equals("true", StringComparison.OrdinalIgnoreCase))
            return false;

        Record(key);
        return true;
    }

    /// <summary>
    /// One line for stderr that names the file and the keys it supplied, or null when it supplied
    /// none. It never includes a value, so a password in the file stays out of the output.
    /// </summary>
    public string? Notice =>
        _used.Count == 0 ? null : $"Using settings from {FilePath}: {string.Join(", ", _used)}";

    private void Record(string key)
    {
        if (!_used.Contains(key, StringComparer.OrdinalIgnoreCase))
            _used.Add(key);
    }
}
