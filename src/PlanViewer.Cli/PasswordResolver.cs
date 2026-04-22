namespace PlanViewer.Cli;

/// <summary>
/// Resolves a SQL Server password from the available CLI inputs:
///   1. --password-stdin (reads one line from redirected stdin)
///   2. --password      (inline CLI arg; emits a stderr warning because it's
///                       visible in process listings, shell history, and audit logs)
///   3. PLANVIEW_PASSWORD environment variable (from the process environment or
///                       a .env file, already looked up by the caller)
/// </summary>
internal static class PasswordResolver
{
    /// <summary>
    /// Returns true with a resolved password (which may be null if no source provided
    /// one). Returns false on user error (mutual-exclusion violation or stdin not
    /// redirected when --password-stdin was requested). The caller is responsible
    /// for setting Environment.ExitCode on failure.
    /// </summary>
    public static bool TryResolve(
        string? inlinePassword,
        bool passwordFromStdin,
        bool stdinAlreadyClaimed,
        string? envPassword,
        out string? password)
    {
        password = null;

        if (passwordFromStdin && !string.IsNullOrEmpty(inlinePassword))
        {
            Console.Error.WriteLine("--password and --password-stdin are mutually exclusive.");
            return false;
        }

        if (passwordFromStdin && stdinAlreadyClaimed)
        {
            Console.Error.WriteLine("--password-stdin can't be combined with --stdin (both read from stdin).");
            return false;
        }

        if (passwordFromStdin)
        {
            if (!Console.IsInputRedirected)
            {
                Console.Error.WriteLine("--password-stdin requires stdin to be redirected (pipe the password into the command).");
                return false;
            }
            password = Console.In.ReadLine()?.TrimEnd('\r', '\n') ?? "";
            return true;
        }

        if (!string.IsNullOrEmpty(inlinePassword))
        {
            Console.Error.WriteLine(
                "Warning: --password is visible in process listings and shell history. " +
                "Prefer --password-stdin, the PLANVIEW_PASSWORD env var, or the credential store.");
            password = inlinePassword;
            return true;
        }

        password = envPassword;
        return true;
    }
}
