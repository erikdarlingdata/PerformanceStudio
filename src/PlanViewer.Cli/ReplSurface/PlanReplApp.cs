using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Services;
using Repl;
using Repl.Mcp;

namespace PlanViewer.Cli.ReplSurface;

public static class PlanReplApp
{
    private static readonly HashSet<string> McpCommandPaths =
    [
        "plan list",
        "plan open {path}",
        "plan {id} summary",
        "plan {id} warnings",
        "plan {id} expensive-operators",
        "plan {id} missing-indexes",
        "plan {id} close"
    ];

    public static ReplApp Create() => Create(new InMemoryPlanCatalog());

    public static ReplApp Create(IPlanCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var operations = new PlanOperations(catalog);
        var app = ReplApp.Create()
            .WithDescription("Interactive SQL Server execution plan analysis.")
            .WithBanner("Open a plan with: plan open <file.sqlplan>")
            .UseDefaultInteractive()
            .UseCliProfile();

        app.MapModule(new PlanReplModule(catalog, operations));
        app.UseMcpServer(options =>
        {
            options.ServerName = "planview";
            options.CommandFilter = command => McpCommandPaths.Contains(command.Path);
            options.AutoPromoteReadOnlyToResources = false;
        });
        return app;
    }
}
