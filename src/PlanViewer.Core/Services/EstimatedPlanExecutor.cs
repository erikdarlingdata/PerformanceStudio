using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace PlanViewer.Core.Services;

/// <summary>
/// Captures an estimated execution plan using SET SHOWPLAN_XML ON.
/// The query is NOT actually executed — SQL Server returns the plan only.
/// Safe for production use.
/// </summary>
public static class EstimatedPlanExecutor
{
    /// <summary>
    /// Gets the estimated execution plan XML for a query without executing it.
    /// </summary>
    public static async Task<string?> GetEstimatedPlanAsync(
        string connectionString,
        string databaseName,
        string queryText,
        int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!string.IsNullOrEmpty(databaseName))
            builder.InitialCatalog = databaseName;

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        // Enable SHOWPLAN XML — subsequent executes return plan, not results
        using (var enableCmd = new SqlCommand("SET SHOWPLAN_XML ON", connection))
        {
            enableCmd.CommandTimeout = timeoutSeconds;
            await enableCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Execute the query — with SHOWPLAN XML ON, this returns the plan
        // as a single-row, single-column result set (no actual execution)
        string? planXml = null;
        using (var queryCmd = new SqlCommand(queryText, connection))
        {
            queryCmd.CommandTimeout = timeoutSeconds;

            await using var registration = cancellationToken.Register(() =>
            {
                try { queryCmd.Cancel(); } catch { /* best effort */ }
            });

            using var reader = await queryCmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var value = reader.GetValue(0)?.ToString();
                if (value != null && value.TrimStart().StartsWith("<ShowPlanXML", StringComparison.Ordinal))
                    planXml = value;
            }
        }

        // Disable SHOWPLAN XML (best effort — connection is about to close)
        try
        {
            using var disableCmd = new SqlCommand("SET SHOWPLAN_XML OFF", connection);
            disableCmd.CommandTimeout = 5;
            await disableCmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch { /* connection cleanup */ }

        return planXml;
    }
}
