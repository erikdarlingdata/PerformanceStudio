using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using PlanViewer.Core.Output;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.Cli.Commands;

using PlanViewer.Cli;

public static class AnalyzeCommand
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
        var fileArg = new Argument<FileInfo?>(
            "file",
            description: "Path to a .sqlplan file, .sql file, or directory of .sql files")
        {
            Arity = ArgumentArity.ZeroOrOne
        };

        var stdinOption = new Option<bool>(
            "--stdin",
            "Read plan XML from stdin");

        var outputOption = new Option<string>(
            "--output",
            getDefaultValue: () => "json",
            description: "Output format: json or text");
        outputOption.AddAlias("-o");

        var compactOption = new Option<bool>(
            "--compact",
            "Compact JSON output (no indentation)");

        var warningsOnlyOption = new Option<bool>(
            "--warnings-only",
            "Only output warnings and missing indexes, skip operator tree");

        // Live execution options
        var serverOption = new Option<string?>(
            "--server",
            "Server name (matches credential store key)");
        serverOption.AddAlias("-s");

        var databaseOption = new Option<string?>(
            "--database",
            "Database context for execution");
        databaseOption.AddAlias("-d");

        var queryOption = new Option<string?>(
            "--query",
            "Inline SQL text to execute");
        queryOption.AddAlias("-q");

        var outputDirOption = new Option<DirectoryInfo?>(
            "--output-dir",
            "Directory for output files (default: current directory)");

        var estimatedOption = new Option<bool>(
            "--estimated",
            "Use estimated plan (SET SHOWPLAN XML ON) instead of actual plan");

        var authOption = new Option<string?>(
            "--auth",
            "Authentication type: windows, sql, entra (default: auto-detect)");

        var trustCertOption = new Option<bool>(
            "--trust-cert",
            "Trust the server certificate (for dev/test environments)");

        var timeoutOption = new Option<int>(
            "--timeout",
            getDefaultValue: () => 60,
            description: "Query timeout in seconds");

        var loginOption = new Option<string?>(
            "--login",
            "SQL Server login name (bypasses credential store)");

        var passwordOption = new Option<string?>(
            "--password",
            "SQL Server password (bypasses credential store)");

        var configOption = new Option<string?>(
            "--config",
            "Path to .planview.json config file (overrides auto-discovery)");

        var cmd = new Command("analyze", "Analyze a SQL Server execution plan")
        {
            fileArg,
            stdinOption,
            outputOption,
            compactOption,
            warningsOnlyOption,
            serverOption,
            databaseOption,
            queryOption,
            outputDirOption,
            estimatedOption,
            authOption,
            trustCertOption,
            timeoutOption,
            loginOption,
            passwordOption,
            configOption
        };

        cmd.SetHandler(async (ctx) =>
        {
            var file = ctx.ParseResult.GetValueForArgument(fileArg);
            var stdin = ctx.ParseResult.GetValueForOption(stdinOption);
            var output = ctx.ParseResult.GetValueForOption(outputOption) ?? "json";
            var compact = ctx.ParseResult.GetValueForOption(compactOption);
            var warningsOnly = ctx.ParseResult.GetValueForOption(warningsOnlyOption);
            var server = ctx.ParseResult.GetValueForOption(serverOption);
            var database = ctx.ParseResult.GetValueForOption(databaseOption);
            var query = ctx.ParseResult.GetValueForOption(queryOption);
            var outputDir = ctx.ParseResult.GetValueForOption(outputDirOption);
            var estimated = ctx.ParseResult.GetValueForOption(estimatedOption);
            var auth = ctx.ParseResult.GetValueForOption(authOption);
            var trustCert = ctx.ParseResult.GetValueForOption(trustCertOption);
            var timeout = ctx.ParseResult.GetValueForOption(timeoutOption);
            var login = ctx.ParseResult.GetValueForOption(loginOption);
            var password = ctx.ParseResult.GetValueForOption(passwordOption);
            var configPath = ctx.ParseResult.GetValueForOption(configOption);

            // Load analyzer config
            var analyzerConfig = ConfigLoader.Load(configPath);

            // Load .env file if present (CLI args take precedence)
            var env = ConnectionHelper.LoadEnvFile();
            server ??= env.GetValueOrDefault("PLANVIEW_SERVER");
            database ??= env.GetValueOrDefault("PLANVIEW_DATABASE");
            login ??= env.GetValueOrDefault("PLANVIEW_LOGIN");
            password ??= env.GetValueOrDefault("PLANVIEW_PASSWORD");
            if (!trustCert && env.GetValueOrDefault("PLANVIEW_TRUST_CERT")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
                trustCert = true;

            if (server != null)
            {
                await RunLiveAsync(file, server, database, query, outputDir, estimated,
                    auth, trustCert, timeout, output, compact, warningsOnly,
                    credentialService, login, password, analyzerConfig);
            }
            else
            {
                await RunAsync(file, stdin, output, compact, warningsOnly, analyzerConfig);
            }
        });

        return cmd;
    }

    #region File/Stdin Analysis (existing)

    private static async Task RunAsync(FileInfo? file, bool stdin, string output, bool compact, bool warningsOnly, AnalyzerConfig analyzerConfig)
    {
        string planXml;
        string source;

        if (stdin || (file == null && Console.IsInputRedirected))
        {
            planXml = await Console.In.ReadToEndAsync();
            source = "stdin";
        }
        else if (file != null)
        {
            if (!file.Exists)
            {
                Console.Error.WriteLine($"File not found: {file.FullName}");
                Environment.ExitCode = 1;
                return;
            }

            planXml = await File.ReadAllTextAsync(file.FullName);
            source = file.Name;
        }
        else
        {
            Console.Error.WriteLine("Provide a .sqlplan file path or use --stdin");
            Environment.ExitCode = 1;
            return;
        }

        if (string.IsNullOrWhiteSpace(planXml))
        {
            Console.Error.WriteLine("Empty plan XML");
            Environment.ExitCode = 1;
            return;
        }

        var plan = ShowPlanParser.Parse(planXml);
        PlanAnalyzer.Analyze(plan, analyzerConfig);

        if (plan.Batches.Count == 0)
        {
            Console.Error.WriteLine("Could not parse any statements from the plan XML");
            Environment.ExitCode = 1;
            return;
        }

        var result = ResultMapper.Map(plan, source);

        if (warningsOnly)
        {
            foreach (var stmt in result.Statements)
                stmt.OperatorTree = null;
        }

        if (output == "text")
        {
            TextFormatter.WriteText(result, Console.Out);
        }
        else
        {
            var opts = compact ? CompactJsonOptions : JsonOptions;
            Console.WriteLine(JsonSerializer.Serialize(result, opts));
        }
    }

    #endregion

    #region Live Execution

    private static async Task RunLiveAsync(
        FileInfo? fileOrDir, string server, string? database, string? query,
        DirectoryInfo? outputDir, bool estimated, string? auth, bool trustCert,
        int timeout, string outputFormat, bool compact, bool warningsOnly,
        ICredentialService? credentialService, string? login, string? password,
        AnalyzerConfig analyzerConfig)
    {
        if (timeout < 0)
        {
            Console.Error.WriteLine("--timeout must be >= 0");
            Environment.ExitCode = 1;
            return;
        }

        if (string.IsNullOrEmpty(database))
        {
            Console.Error.WriteLine("--database is required when using --server");
            Environment.ExitCode = 1;
            return;
        }

        // Build connection string
        string connectionString;
        if (!string.IsNullOrEmpty(login))
        {
            // Direct login/password — bypass credential store entirely
            connectionString = ConnectionHelper.BuildConnectionString(server, database, login, password ?? "", trustCert, multipleActiveResultSets: true);
        }
        else if (credentialService != null)
        {
            try
            {
                var connection = BuildServerConnection(server, auth, trustCert, credentialService);
                connectionString = connection.GetConnectionString(credentialService, database);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.ExitCode = 1;
                return;
            }
        }
        else
        {
            Console.Error.WriteLine("No credentials provided. Use --login/--password, a .env file, or the credential store.");
            Environment.ExitCode = 1;
            return;
        }

        // Determine inputs
        var sqlInputs = new List<(string Name, string SqlText)>();

        if (!string.IsNullOrEmpty(query))
        {
            sqlInputs.Add(("inline-query", query));
        }
        else if (fileOrDir == null)
        {
            Console.Error.WriteLine("Provide a .sql file, directory, or --query with --server");
            Environment.ExitCode = 1;
            return;
        }
        else if (Directory.Exists(fileOrDir.FullName))
        {
            // Directory: batch all *.sql files
            var sqlFiles = Directory.GetFiles(fileOrDir.FullName, "*.sql")
                .OrderBy(f => f)
                .ToArray();

            if (sqlFiles.Length == 0)
            {
                Console.Error.WriteLine($"No .sql files found in {fileOrDir.FullName}");
                Environment.ExitCode = 1;
                return;
            }

            foreach (var sqlFile in sqlFiles)
            {
                var sqlText = await File.ReadAllTextAsync(sqlFile);
                sqlInputs.Add((Path.GetFileNameWithoutExtension(sqlFile), sqlText));
            }
        }
        else if (fileOrDir.Exists)
        {
            var ext = fileOrDir.Extension.ToLowerInvariant();
            if (ext == ".sqlplan")
            {
                // Redirect to existing file analysis path
                await RunAsync(fileOrDir, false, outputFormat, compact, warningsOnly, analyzerConfig);
                return;
            }
            if (ext != ".sql")
            {
                Console.Error.WriteLine($"Unsupported file type: {ext}. Use .sql or .sqlplan");
                Environment.ExitCode = 1;
                return;
            }
            var text = await File.ReadAllTextAsync(fileOrDir.FullName);
            sqlInputs.Add((Path.GetFileNameWithoutExtension(fileOrDir.Name), text));
        }
        else
        {
            Console.Error.WriteLine($"File or directory not found: {fileOrDir.FullName}");
            Environment.ExitCode = 1;
            return;
        }

        // Resolve output directory
        var outDir = outputDir?.FullName ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outDir);

        var isAzure = IsAzureSqlDb(server);
        var planType = estimated ? "estimated" : "actual";
        Console.Error.WriteLine($"Capturing {planType} plans from {server}/{database}");
        Console.Error.WriteLine();

        // Process each SQL input sequentially
        var total = sqlInputs.Count;
        var errors = 0;

        for (int i = 0; i < total; i++)
        {
            var (name, sqlText) = sqlInputs[i];
            var label = total > 1 ? $"[{i + 1}/{total}] {name}" : name;

            try
            {
                Console.Error.Write($"{label} ... ");
                var sw = Stopwatch.StartNew();

                // Capture plan
                string? planXml;
                if (estimated)
                {
                    planXml = await EstimatedPlanExecutor.GetEstimatedPlanAsync(
                        connectionString, database, sqlText, timeout);
                }
                else
                {
                    planXml = await ActualPlanExecutor.ExecuteForActualPlanAsync(
                        connectionString, database, sqlText,
                        planXml: null, isolationLevel: null,
                        isAzureSqlDb: isAzure, timeoutSeconds: timeout,
                        CancellationToken.None);
                }

                sw.Stop();

                if (string.IsNullOrEmpty(planXml))
                {
                    Console.Error.WriteLine($"NO PLAN ({sw.Elapsed.TotalSeconds:F1}s)");
                    errors++;
                    continue;
                }

                // Write .sqlplan file
                var planPath = Path.Combine(outDir, $"{name}.sqlplan");
                await File.WriteAllTextAsync(planPath, planXml);

                // Parse, analyze, map result
                var plan = ShowPlanParser.Parse(planXml);
                PlanAnalyzer.Analyze(plan, analyzerConfig);
                var result = ResultMapper.Map(plan, $"{name}.sql");

                if (warningsOnly)
                {
                    foreach (var stmt in result.Statements)
                        stmt.OperatorTree = null;
                }

                if (outputFormat == "json" || outputFormat == "both")
                {
                    var jsonOpts = compact ? CompactJsonOptions : JsonOptions;
                    var json = JsonSerializer.Serialize(result, jsonOpts);
                    var analysisPath = Path.Combine(outDir, $"{name}.analysis.json");
                    await File.WriteAllTextAsync(analysisPath, json);
                }

                if (outputFormat == "text" || outputFormat == "both")
                {
                    var txtPath = Path.Combine(outDir, $"{name}.analysis.txt");
                    using var writer = new StreamWriter(txtPath);
                    TextFormatter.WriteText(result, writer);
                }

                Console.Error.WriteLine($"OK ({sw.Elapsed.TotalSeconds:F1}s)");
            }
            catch (SqlException ex)
            {
                Console.Error.WriteLine($"SQL ERROR: {ex.Message}");
                errors++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR: {ex.Message}");
                errors++;
            }
        }

        // Summary
        if (total > 1)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Processed {total} files: {total - errors} succeeded, {errors} failed");
        }
        Console.Error.WriteLine($"Output: {outDir}");

        if (errors > 0)
            Environment.ExitCode = 1;
    }

    private static ServerConnection BuildServerConnection(
        string server, string? auth, bool trustCert, ICredentialService credentialService)
    {
        var authType = auth?.ToLowerInvariant() switch
        {
            "windows" => AuthenticationTypes.Windows,
            "sql" => AuthenticationTypes.SqlServer,
            "entra" => AuthenticationTypes.EntraMFA,
            null => credentialService.CredentialExists(server)
                ? AuthenticationTypes.SqlServer
                : AuthenticationTypes.Windows,
            _ => throw new ArgumentException($"Unknown auth type: {auth}. Use: windows, sql, entra")
        };

        if (authType == AuthenticationTypes.SqlServer && !credentialService.CredentialExists(server))
        {
            Console.Error.WriteLine($"No credential found for {server}. Run: planview credential add {server} --user <username>");
            Environment.ExitCode = 1;
            throw new InvalidOperationException("No credentials configured");
        }

        return new ServerConnection
        {
            Id = server,
            ServerName = server,
            DisplayName = server,
            AuthenticationType = authType,
            TrustServerCertificate = trustCert,
            EncryptMode = trustCert ? "Optional" : "Mandatory"
        };
    }

    private static bool IsAzureSqlDb(string serverName)
    {
        return serverName.Contains(".database.windows.net", StringComparison.OrdinalIgnoreCase) ||
               serverName.Contains(".database.azure.com", StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
