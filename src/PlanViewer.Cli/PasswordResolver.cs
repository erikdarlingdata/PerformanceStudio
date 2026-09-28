namespace PlanViewer.Cli;

/// <summary>
/// Resolves a SQL Server password from the available CLI inputs:
///   1. --password-stdin (reads one line from redirected stdin)
///   2. --password      (inline CLI arg; emits a stderr warning because it's
///                       visible in process listings, shell history, and audit logs)
///   3. PLANVIEW_PASSWORD from the .env file (asked for only when neither option
///                       above gave a password, so the caller knows whether the file supplied it)
/// </summary>
internal static class PasswordResolver
{
    /// <summary>
    /// Returns true with a resolved password (which may be null if no source provided
    /// one). Returns false on user error (mutual-exclusion violation or stdin not
    /// redirected when --password-stdin was requested). The caller is responsible
    /// for setting Environment.ExitCode on failure. Messages go to <paramref name="error"/>,
    /// or to stderr when it is null.
    /// </summary>
    public static bool TryResolve(
        string? inlinePassword,
        bool passwordFromStdin,
        bool stdinAlreadyClaimed,
        Func<string?> envPassword,
        out string? password,
        TextWriter? error = null)
    {
        password = null;
        var err = error ?? Console.Error;

        if (passwordFromStdin && !string.IsNullOrEmpty(inlinePassword))
        {
            err.WriteLine("--password and --password-stdin are mutually exclusive.");
            return false;
        }

        if (passwordFromStdin && stdinAlreadyClaimed)
        {
            err.WriteLine("--password-stdin can't be combined with --stdin (both read from stdin).");
            return false;
        }

        if (passwordFromStdin)
        {
            if (!Console.IsInputRedirected)
            {
                err.WriteLine("--password-stdin requires stdin to be redirected (pipe the password into the command).");
                return false;
            }
            password = Console.In.ReadLine()?.TrimEnd('\r', '\n') ?? "";
            return true;
        }

        if (!string.IsNullOrEmpty(inlinePassword))
        {
            err.WriteLine(
                "Warning: --password is visible in process listings and shell history. " +
                "Prefer --password-stdin, PLANVIEW_PASSWORD in a .env file, or the credential store.");
            password = inlinePassword;
            return true;
        }

        password = envPassword();
        return true;
    }
}
