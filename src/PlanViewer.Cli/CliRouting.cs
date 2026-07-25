namespace PlanViewer.Cli;

public static class CliRouting
{
    private static readonly HashSet<string> ReplCommands =
        new(StringComparer.OrdinalIgnoreCase) { "plan", "open", "mcp", "repl" };

    public static bool ShouldUseRepl(IReadOnlyList<string> args) =>
        args.Count > 0 && ReplCommands.Contains(args[0]);

    public static string[] GetReplArgs(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals("repl", StringComparison.OrdinalIgnoreCase)
            ? args.Skip(1).ToArray()
            : args.ToArray();
}
