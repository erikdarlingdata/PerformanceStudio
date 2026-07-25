using PlanViewer.Cli;

namespace PlanViewer.Core.Tests;

public sealed class CliRoutingTests
{
    [Theory]
    [InlineData("analyze")]
    [InlineData("query-store")]
    [InlineData("credential")]
    [InlineData("--help")]
    public void LegacyCommands_RemainOnSystemCommandLine(string command) =>
        Assert.False(CliRouting.ShouldUseRepl([command]));

    [Theory]
    [InlineData("plan")]
    [InlineData("open")]
    [InlineData("mcp")]
    [InlineData("repl")]
    public void ReplCommands_UseTheReplGraph(string command) =>
        Assert.True(CliRouting.ShouldUseRepl([command]));

    [Fact]
    public void NoArguments_PreserveLegacyRouting() =>
        Assert.False(CliRouting.ShouldUseRepl([]));

    [Fact]
    public void ReplAlias_IsRemovedBeforeDispatch() =>
        Assert.Equal(["plan", "list"], CliRouting.GetReplArgs(["repl", "plan", "list"]));
}
