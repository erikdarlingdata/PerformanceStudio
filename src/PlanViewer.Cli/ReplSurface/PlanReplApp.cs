using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Services;
using Repl;
using Repl.Mcp;

namespace PlanViewer.Cli.ReplSurface;

public static class PlanReplApp
{
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
        app.UseMcpServer(options => options.ServerName = "PerformanceStudio");
        return app;
    }
}
