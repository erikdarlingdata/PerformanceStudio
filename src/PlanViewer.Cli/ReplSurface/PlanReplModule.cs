using System.ComponentModel;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
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
        (string path, CancellationToken cancellationToken, IMcpClientRoots? roots = null) =>
                    OpenAsync(path, roots, cancellationToken))
            .WithDescription("Open a SQL Server execution plan");

        map.Context("plan", plan =>
        {
            plan.Map("list", () => catalog.GetAllSessions())
                .WithDescription("List plans loaded in this process")
                .ReadOnly();

            plan.Map(
                    "open {path}",
                    [Description("Open a .sqlplan file and enter its plan context")]
            (string path, CancellationToken cancellationToken, IMcpClientRoots? roots = null) =>
                        OpenAsync(path, roots, cancellationToken))
                .WithDescription("Open a SQL Server execution plan");

            plan.Context(
                "{id}",
                scope =>
                {
                    scope.Map("summary", (string id) => Execute(() => operations.GetSummary(id)))
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

                    scope.Map("missing-indexes", (string id) => Execute(() => operations.GetMissingIndexes(id)))
                        .WithDescription("List missing-index suggestions")
                        .ReadOnly();

                    scope.Map("close", (string id) => Close(id))
                        .WithDescription("Close this in-memory plan session");
                },
                validation: (string id) => catalog.GetSession(id) is not null);
        });
    }

    private async Task<object> OpenAsync(
        string path,
        IMcpClientRoots? roots,
        CancellationToken cancellationToken)
    {
        try
        {
            PlanSessionSummary opened;
            if (roots is null)
            {
                opened = await operations.OpenAsync(path, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                opened = await operations.OpenOwnedStreamAsync(
                    async token =>
                    {
                        var authorized = await McpPlanPathPolicy
                            .OpenAsync(path, roots, token)
                            .ConfigureAwait(false);
                        return (authorized.Stream, authorized.Label, (IAsyncDisposable)authorized);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            return Results.NavigateTo($"plan {opened.SessionId}", opened);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Error("path_not_allowed", "Plan path is outside the allowed roots.");
        }
        catch (FileNotFoundException ex)
        {
            return roots is null
                ? Results.NotFound(ex.Message)
                : Results.NotFound("Plan file was not found within the allowed roots.");
        }
        catch (IOException ex)
        {
            return roots is null
                ? Results.Error("file_error", ex.Message)
                : Results.Error("file_error", "Plan file could not be read within the allowed roots.");
        }
        catch (ArgumentException ex)
        {
            return roots is null
                ? Results.Error("invalid_path", ex.Message)
                : Results.Error("invalid_path", "Plan path is invalid.");
        }
        catch (InvalidDataException ex)
        {
            return Results.Error("invalid_plan", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Error("session_limit", ex.Message);
        }
    }

    private object Close(string id)
    {
        if (!operations.Close(id))
            return Results.NotFound("Plan session was not found.");

        return Results.NavigateTo("plan", new PlanCloseResult { SessionId = id, Closed = true });
    }

    private static object Execute<T>(Func<T> operation)
        where T : notnull
    {
        try
        {
            return operation();
        }
        catch (ArgumentException ex)
        {
            return Results.Error("invalid_argument", ex.Message);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound("Plan session was not found.");
        }
    }
}
