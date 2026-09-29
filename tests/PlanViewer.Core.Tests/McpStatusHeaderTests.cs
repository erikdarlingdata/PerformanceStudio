using PlanViewer.App;

namespace PlanViewer.Core.Tests;

/// <summary>
/// MainWindow.BuildMcpStatusHeader is the one place the exact wording of the MCP status menu
/// item is decided, split out — the same way CloseAction and DecideClose are — so the three
/// outcomes can be pinned without a window, a port, or a real McpHostService behind them. See
/// McpHostServiceTests for what actually decides Running versus Failed.
/// </summary>
public class McpStatusHeaderTests
{
    [Fact]
    public void StartingNamesThePort()
    {
        Assert.Equal(
            "MCP Server: Starting (port 5150)",
            MainWindow.BuildMcpStatusHeader(MainWindow.McpServerStatus.Starting, 5150));
    }

    [Fact]
    public void RunningNamesThePort()
    {
        Assert.Equal(
            "MCP Server: Running (port 5150)",
            MainWindow.BuildMcpStatusHeader(MainWindow.McpServerStatus.Running, 5150));
    }

    [Fact]
    public void FailedNamesTheReasonInsteadOfThePort()
    {
        Assert.Equal(
            "MCP Server: Failed (port 5150 is in use)",
            MainWindow.BuildMcpStatusHeader(MainWindow.McpServerStatus.Failed, 5150, "port 5150 is in use"));
    }

    [Fact]
    public void AnUnderscoreInTheReasonIsShownAsWritten()
    {
        Assert.Equal(
            "MCP Server: Failed (port 5150: bad MCP__PORT value)",
            MainWindow.BuildMcpStatusHeader(MainWindow.McpServerStatus.Failed, 5150, "port 5150: bad MCP_PORT value"));
    }
}
