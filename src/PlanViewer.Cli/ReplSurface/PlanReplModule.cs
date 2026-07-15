using System.ComponentModel;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Services;
using Repl;
using Repl.Mcp;

namespace PlanViewer.Cli.ReplSurface;

public sealed class PlanReplModule(IPlanCatalog catalog, PlanOperations operations) : IReplModule
{
    public void Map(IReplMap map)
    {
        map.Map(
                "open {path}",
                [Description("Open a .sqlplan file and enter its plan context")]
        (string path, CancellationToken cancellationToken) => OpenAsync(path, cancellationToken))
            .WithDescription("Open a SQL Server execution plan")
            .ReadOnly();

        map.Context("plan", plan =>
        {
            plan.Map("list", () => catalog.GetAllSessions())
                .WithDescription("List plans loaded in this process")
                .ReadOnly();

            plan.Map(
                    "open {path}",
                    [Description("Open a .sqlplan file and enter its plan context")]
            (string path, CancellationToken cancellationToken) => OpenAsync(path, cancellationToken))
                .WithDescription("Open a SQL Server execution plan")
                .ReadOnly();

            plan.Context(
                "{id}",
                scope =>
                {
                    scope.Map("summary", (string id) => operations.GetSummary(id))
                        .WithDescription("Show a concise plan summary")
                        .ReadOnly();

                    scope.Map(
                            "warnings",
                            (string id, [Description("Critical, Warning, or Info")] string? severity = null) =>
                                Execute(() => operations.GetWarnings(id, severity)))
                        .WithDescription("List plan warnings, optionally filtered by severity")
                        .ReadOnly();

                    scope.Map(
                            "expensive-operators",
                            (string id, [Description("Number of operators to return (1-100)")] int top = 10) =>
                                Execute(() => operations.GetExpensiveOperators(id, top)))
                        .WithDescription("List the most expensive operators")
                        .ReadOnly();

                    scope.Map(
                            "operators",
                            (string id, [Description("Number of operators to return (1-100)")] int top = 10) =>
                                Execute(() => operations.GetExpensiveOperators(id, top)))
                        .WithDescription("Alias for expensive-operators")
                        .ReadOnly();

                    scope.Map("missing-indexes", (string id) => operations.GetMissingIndexes(id))
                        .WithDescription("List missing-index suggestions")
                        .ReadOnly();
                },
                validation: (string id) => catalog.GetSession(id) is not null);
        });
    }

    private async Task<object> OpenAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var opened = await operations.OpenAsync(path, cancellationToken).ConfigureAwait(false);
            return Results.NavigateTo($"plan {opened.SessionId}", opened);
        }
        catch (FileNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return Results.Error("invalid_plan", ex.Message);
        }
    }

    private static object Execute<T>(Func<T> operation)
    {
        try
        {
            return operation()!;
        }
        catch (ArgumentException ex)
        {
            return Results.Error("invalid_argument", ex.Message);
        }
    }
}
