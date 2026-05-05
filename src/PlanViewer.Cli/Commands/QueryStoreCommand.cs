using System.CommandLine;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using PlanViewer.Core.Output;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.Cli.Commands;

public static class QueryStoreCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static Command Create(ICredentialService? credentialService = null)
    {
        var serverOption = new Option<string>("--server", "-s")
        {
            Description = "SQL Server instance name",
            Required = true
        };

        var databaseOption = new Option<string>("--database", "-d")
        {
            Description = "Database with Query Store enabled",
            Required = true
        };

        var topOption = new Option<int>("--top")
        {
            Description = "Number of top queries to analyze",
            DefaultValueFactory = _ => 25
        };

        var orderByOption = new Option<string>("--order-by")
        {
            Description = "Ranking metric (total or avg): cpu, avg-cpu, duration, avg-duration, reads, avg-reads, writes, avg-writes, physical-reads, avg-physical-reads, memory, avg-memory, executions",
            DefaultValueFactory = _ => "cpu"
        };

        var hoursBackOption = new Option<int>("--hours-back")
        {
            Description = "Hours of history to analyze",
            DefaultValueFactory = _ => 24
        };

        var outputDirOption = new Option<DirectoryInfo?>("--output-dir")
        {
            Description = "Directory for output files (default: current directory)"
        };

        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output format: json or text",
            DefaultValueFactory = _ => "text"
        };

        var compactOption = new Option<bool>("--compact")
        {
            Description = "Compact JSON output"
        };

        var warningsOnlyOption = new Option<bool>("--warnings-only")
        {
            Description = "Skip operator tree in output"
        };

        var configOption = new Option<string?>("--config")
        {
            Description = "Path to .planview.json config file"
        };

        var authOption = new Option<string?>("--auth")
        {
            Description = "Authentication: windows, sql, entra"
        };

        var trustCertOption = new Option<bool>("--trust-cert")
        {
            Description = "Trust the server certificate"
        };

        var loginOption = new Option<string?>("--login")
        {
            Description = "SQL Server login (bypasses credential store)"
        };

        var passwordOption = new Option<string?>("--password")
        {
            Description = "SQL Server password. Visible in process listings — prefer --password-stdin."
        };

        var passwordStdinOption = new Option<bool>("--password-stdin")
        {
            Description = "Read the SQL Server password from stdin (avoids process-listing exposure). Mutually exclusive with --password."
        };

        var queryIdOption = new Option<long?>("--query-id")
        {
            Description = "Filter by Query Store query ID"
        };

        var planIdOption = new Option<long?>("--plan-id")
        {
            Description = "Filter by Query Store plan ID"
        };

        var queryHashOption = new Option<string?>("--query-hash")
        {
            Description = "Filter by query hash (hex, e.g. 0x1AB2C3D4)"
        };

        var planHashOption = new Option<string?>("--plan-hash")
        {
            Description = "Filter by query plan hash (hex, e.g. 0x1AB2C3D4)"
        };

        var moduleOption = new Option<string?>("--module")
        {
            Description = "Filter by module name (schema.name, supports % wildcards)"
        };

        var cmd = new Command("query-store", "Analyze top queries from Query Store")
        {
            serverOption, databaseOption, topOption, orderByOption, hoursBackOption,
            outputDirOption, outputOption, compactOption, warningsOnlyOption, configOption,
            authOption, trustCertOption, loginOption, passwordOption, passwordStdinOption,
            queryIdOption, planIdOption, queryHashOption, planHashOption, moduleOption
        };

        cmd.SetAction(async (parseResult, ct) =>
        {
            var server = parseResult.GetValue(serverOption)!;
            var database = parseResult.GetValue(databaseOption)!;
            var top = parseResult.GetValue(topOption);
            var orderBy = parseResult.GetValue(orderByOption) ?? "cpu";
            var hoursBack = parseResult.GetValue(hoursBackOption);
            var outputDir = parseResult.GetValue(outputDirOption);
            var output = parseResult.GetValue(outputOption) ?? "text";
            var compact = parseResult.GetValue(compactOption);
            var warningsOnly = parseResult.GetValue(warningsOnlyOption);
            var configPath = parseResult.GetValue(configOption);
            var auth = parseResult.GetValue(authOption);
            var trustCert = parseResult.GetValue(trustCertOption);
            var login = parseResult.GetValue(loginOption);
            var passwordInline = parseResult.GetValue(passwordOption);
            var passwordStdin = parseResult.GetValue(passwordStdinOption);
            var filterQueryId = parseResult.GetValue(queryIdOption);
            var filterPlanId = parseResult.GetValue(planIdOption);
            var filterQueryHash = parseResult.GetValue(queryHashOption);
            var filterPlanHash = parseResult.GetValue(planHashOption);
            var filterModule = parseResult.GetValue(moduleOption);

            // Load .env file if present (CLI args take precedence)
            var env = ConnectionHelper.LoadEnvFile();
            login ??= env.GetValueOrDefault("PLANVIEW_LOGIN");
            if (!trustCert && env.GetValueOrDefault("PLANVIEW_TRUST_CERT")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
                trustCert = true;

            // Resolve password from --password-stdin, --password, or PLANVIEW_PASSWORD
            if (!PasswordResolver.TryResolve(
                    passwordInline, passwordStdin, stdinAlreadyClaimed: false,
                    env.GetValueOrDefault("PLANVIEW_PASSWORD"),
                    out var password))
            {
                Environment.ExitCode = 1;
                return;
            }

            if (top < 1)
            {
                Console.Error.WriteLine("--top must be >= 1");
                Environment.ExitCode = 1;
                return;
            }

            if (hoursBack < 1)
            {
                Console.Error.WriteLine("--hours-back must be >= 1");
                Environment.ExitCode = 1;
                return;
            }

            QueryStoreFilter? filter = null;
            if (filterQueryId != null || filterPlanId != null ||
                filterQueryHash != null || filterPlanHash != null || filterModule != null)
            {
                filter = new QueryStoreFilter
                {
                    QueryId = filterQueryId,
                    PlanId = filterPlanId,
                    QueryHash = filterQueryHash,
                    QueryPlanHash = filterPlanHash,
                    ModuleName = filterModule,
                };
            }

            var analyzerConfig = ConfigLoader.Load(configPath);

            // Build connection string
            string connectionString;
            if (!string.IsNullOrEmpty(login))
            {
                connectionString = ConnectionHelper.BuildConnectionString(server, database, login, password ?? "", trustCert);
            }
            else if (credentialService != null)
            {
                var authType = auth?.ToLowerInvariant() switch
                {
                    "windows" => AuthenticationTypes.Windows,
                    "sql" => AuthenticationTypes.SqlServer,
                    "entra" => AuthenticationTypes.EntraMFA,
                    null => credentialService.CredentialExists(server)
                        ? AuthenticationTypes.SqlServer
                        : AuthenticationTypes.Windows,
                    _ => throw new ArgumentException($"Unknown auth type: {auth}")
                };

                var conn = new ServerConnection
                {
                    Id = server, ServerName = server, DisplayName = server,
                    AuthenticationType = authType,
                    TrustServerCertificate = trustCert,
                    EncryptMode = trustCert ? "Optional" : "Mandatory"
                };
                connectionString = conn.GetConnectionString(credentialService, database);
            }
            else
            {
                Console.Error.WriteLine("No credentials. Use --login/--password or the credential store.");
                Environment.ExitCode = 1;
                return;
            }

            try
            {
                await RunAsync(connectionString, server, database, top, orderBy, hoursBack,
                    outputDir, output, compact, warningsOnly, analyzerConfig, filter);
            }
            catch (SqlException ex)
            {
                Console.Error.WriteLine($"SQL Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        });

        return cmd;
    }

    private static async Task RunAsync(
        string connectionString, string server, string database,
        int top, string orderBy, int hoursBack,
        DirectoryInfo? outputDir, string outputFormat, bool compact, bool warningsOnly,
        AnalyzerConfig analyzerConfig, QueryStoreFilter? filter = null)
    {
        // Verify Query Store is enabled
        Console.Error.Write($"Checking Query Store on {server}/{database}... ");
        var (enabled, state) = await QueryStoreService.CheckEnabledAsync(connectionString);
        if (!enabled)
        {
            Console.Error.WriteLine($"NOT ENABLED (state: {state ?? "unknown"})");
            Console.Error.WriteLine("Enable Query Store: ALTER DATABASE [" + database + "] SET QUERY_STORE = ON;");
            Environment.ExitCode = 1;
            return;
        }
        Console.Error.WriteLine($"OK ({state})");

        // Fetch plans
        Console.Error.Write($"Fetching top {top} queries by {orderBy} (last {hoursBack}h)... ");
        var plans = await QueryStoreService.FetchTopPlansAsync(
            connectionString, top, orderBy, hoursBack, filter);

        if (plans.Count == 0)
        {
            Console.Error.WriteLine("no data");
            Console.Error.WriteLine("No Query Store data found for the specified time range.");
            return;
        }
        Console.Error.WriteLine($"{plans.Count} plans");
        Console.Error.WriteLine();

        // Fetch server metadata for Rule 38 (Standard Edition DOP limitation)
        ServerMetadata? serverMetadata = null;
        try
        {
            var isAzure = server.Contains(".database.windows.net", StringComparison.OrdinalIgnoreCase) ||
                          server.Contains(".database.azure.com", StringComparison.OrdinalIgnoreCase);
            serverMetadata = await ServerMetadataService.FetchServerMetadataAsync(connectionString, isAzure);
        }
        catch
        {
            // Non-fatal: analysis continues without server context
        }

        // Resolve output directory
        var outDir = outputDir?.FullName ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outDir);

        // Summary tracking — show the primary sort metric column
        var (metricHeader, metricFmt) = GetMetricFormatter(orderBy);

        var summaryLines = new List<string>();
        summaryLines.Add($"=== Query Store Analysis: {database} ({plans.Count} queries, top by {orderBy}, last {hoursBack}h) ===");
        summaryLines.Add("");
        summaryLines.Add(string.Format(" {0,-4} {1,-10} {2,-10} {3,-20} {4,-20} {5,14} {6,12} {7,8} {8,8}",
            "#", "Query ID", "Plan ID", "Query Hash", "Module", metricHeader, "Executions", "Warns", "Crit"));
        summaryLines.Add(new string('-', 130));

        for (int i = 0; i < plans.Count; i++)
        {
            var qsPlan = plans[i];
            var label = $"query_{qsPlan.QueryId}_plan_{qsPlan.PlanId}";

            try
            {
                Console.Error.Write($"[{i + 1}/{plans.Count}] Query {qsPlan.QueryId} / Plan {qsPlan.PlanId}... ");

                // Save .sqlplan
                var planPath = Path.Combine(outDir, $"{label}.sqlplan");
                await File.WriteAllTextAsync(planPath, qsPlan.PlanXml);

                // Parse, analyze, map
                var plan = ShowPlanParser.Parse(qsPlan.PlanXml);
                PlanAnalyzer.Analyze(plan, analyzerConfig, serverMetadata);
                BenefitScorer.Score(plan);
                var result = ResultMapper.Map(plan, $"{label}.sqlplan");

                if (warningsOnly)
                {
                    foreach (var stmt in result.Statements)
                        stmt.OperatorTree = null;
                }

                // Write analysis files
                if (outputFormat == "json" || outputFormat == "both")
                {
                    var jsonOpts = compact ? CompactJsonOptions : JsonOptions;
                    var json = JsonSerializer.Serialize(result, jsonOpts);
                    await File.WriteAllTextAsync(Path.Combine(outDir, $"{label}.analysis.json"), json);
                }

                if (outputFormat == "text" || outputFormat == "both")
                {
                    var txtPath = Path.Combine(outDir, $"{label}.analysis.txt");
                    using var writer = new StreamWriter(txtPath);
                    TextFormatter.WriteText(result, writer);
                }

                var warnings = result.Summary.TotalWarnings;
                var critical = result.Summary.CriticalWarnings;
                var metricValue = metricFmt(qsPlan);

                var moduleName = string.IsNullOrEmpty(qsPlan.ModuleName) ? "(ad hoc)" : qsPlan.ModuleName;
                summaryLines.Add(string.Format(" {0,-4} {1,-10} {2,-10} {3,-20} {4,-20} {5,14} {6,12:N0} {7,8} {8,8}",
                    i + 1, qsPlan.QueryId, qsPlan.PlanId, qsPlan.QueryHash,
                    moduleName.Length > 18 ? moduleName[..18] + ".." : moduleName,
                    metricValue, qsPlan.CountExecutions, warnings, critical));

                Console.Error.WriteLine($"OK ({warnings} warnings, {critical} critical)");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR: {ex.Message}");
                summaryLines.Add(string.Format(" {0,-4} {1,-10} {2,-10} {3,-20} {4,-20} ERROR: {5}",
                    i + 1, qsPlan.QueryId, qsPlan.PlanId, qsPlan.QueryHash, "", ex.Message));
            }
        }

        // Write summary
        summaryLines.Add("");
        var summaryPath = Path.Combine(outDir, "summary.txt");
        await File.WriteAllLinesAsync(summaryPath, summaryLines);

        Console.Error.WriteLine();
        Console.Error.WriteLine($"Output: {outDir}");
        Console.Error.WriteLine($"Summary: {summaryPath}");
    }

    private static (string Header, Func<QueryStorePlan, string> Format) GetMetricFormatter(string orderBy)
    {
        static string FmtMs(double us) => $"{us / 1000.0:N1}ms";
        static string FmtPages(double pages) => $"{pages:N0}pg";
        static string FmtTotalMs(long us) => $"{us / 1000.0:N0}ms";
        static string FmtTotalPages(long pages) => $"{pages:N0}pg";
        static string FmtCount(long n) => $"{n:N0}";

        return orderBy.ToLowerInvariant() switch
        {
            "cpu"              => ("Total CPU",      p => FmtTotalMs(p.TotalCpuTimeUs)),
            "avg-cpu"          => ("Avg CPU",        p => FmtMs(p.AvgCpuTimeUs)),
            "duration"         => ("Total Duration", p => FmtTotalMs(p.TotalDurationUs)),
            "avg-duration"     => ("Avg Duration",   p => FmtMs(p.AvgDurationUs)),
            "reads"            => ("Total Reads",    p => FmtTotalPages(p.TotalLogicalIoReads)),
            "avg-reads"        => ("Avg Reads",      p => FmtPages(p.AvgLogicalIoReads)),
            "writes"           => ("Total Writes",   p => FmtTotalPages(p.TotalLogicalIoWrites)),
            "avg-writes"       => ("Avg Writes",     p => FmtPages(p.AvgLogicalIoWrites)),
            "physical-reads"   => ("Total Phys Rds", p => FmtTotalPages(p.TotalPhysicalIoReads)),
            "avg-physical-reads" => ("Avg Phys Rds", p => FmtPages(p.AvgPhysicalIoReads)),
            "memory"           => ("Total Mem Grant", p => FmtTotalPages(p.TotalMemoryGrantPages)),
            "avg-memory"       => ("Avg Mem Grant",  p => FmtPages(p.AvgMemoryGrantPages)),
            "executions"       => ("Executions",     p => FmtCount(p.CountExecutions)),
            _                  => ("Total CPU",      p => FmtTotalMs(p.TotalCpuTimeUs))
        };
    }

}
