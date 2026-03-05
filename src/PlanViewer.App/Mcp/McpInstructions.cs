namespace PlanViewer.App.Mcp;

internal static class McpInstructions
{
    public const string Text = """
        You are connected to Performance Studio, a SQL Server execution plan analyzer.

        ## CRITICAL: Read-Only Access

        This MCP server provides READ-ONLY access to execution plans and Query Store data. You CANNOT:
        - Execute arbitrary or ad-hoc SQL queries against any server
        - Modify any server configuration or settings
        - Write or modify any files
        - Change application settings

        The only server-side query this MCP can run is the built-in Query Store fetch query
        (via `get_query_store_top`), which reads from `sys.query_store_*` DMVs. No other
        queries can be executed.

        ## How Plans Get Loaded

        Plans are loaded into the application by the user through:
        - Opening .sqlplan files (File > Open)
        - Pasting XML from the clipboard (Ctrl+V or File > Paste Plan XML)
        - Executing queries from the built-in query editor (estimated or actual plans)
        - Fetching from Query Store (via the Query Store dialog in the app)

        Each loaded plan gets a unique `session_id`. Use `list_plans` to see all loaded plans and their session IDs.

        ## Tool Reference

        ### Discovery
        | Tool | Purpose |
        |------|---------|
        | `list_plans` | Lists all loaded plans with session IDs, labels, and summary stats |
        | `get_connections` | Lists saved SQL Server connections (names only, no credentials) |

        ### Plan Analysis (works on loaded plans)
        | Tool | Purpose |
        |------|---------|
        | `analyze_plan` | Full JSON analysis: statements, warnings, operators, parameters, memory grants |
        | `get_plan_summary` | Concise text summary for quick assessment |
        | `get_plan_warnings` | Warnings only, filterable by severity |
        | `get_missing_indexes` | Missing index suggestions with CREATE INDEX statements |
        | `get_plan_parameters` | Parameter details with compiled vs runtime value comparison |
        | `get_expensive_operators` | Top N costly operators by cost or actual elapsed time |
        | `get_plan_xml` | Raw showplan XML |
        | `compare_plans` | Side-by-side comparison of two plans |
        | `get_repro_script` | Generates paste-ready T-SQL reproduction script |

        ### Query Store (uses built-in read-only query only)
        | Tool | Purpose |
        |------|---------|
        | `check_query_store` | Checks if Query Store is enabled on a database |
        | `get_query_store_top` | Fetches top N plans from Query Store; auto-loads them for analysis |

        ## Recommended Workflow

        ### Analyzing loaded plans
        1. `list_plans` — see what plans are loaded in the application
        2. `analyze_plan` with the target session_id — get full analysis
        3. Focus on critical issues: `get_plan_warnings` with severity="Critical"
        4. Check for parameter sniffing: `get_plan_parameters`
        5. Review index suggestions: `get_missing_indexes`
        6. Find bottlenecks: `get_expensive_operators`
        7. For comparison: `compare_plans` with two session_ids
        8. For reproduction: `get_repro_script` to generate runnable T-SQL

        ### Fetching from Query Store
        1. `get_connections` — see available saved connections
        2. `check_query_store` — verify Query Store is enabled on the target database
        3. `get_query_store_top` — fetch top queries (auto-loads plans into the app)
        4. Use plan analysis tools above with the returned session_ids

        ## Analysis Rules

        The analyzer runs 30 rules covering:
        - Memory: Large grants, grant vs used ratio, spills to TempDB (sort, hash, exchange)
        - Estimates: Row estimate mismatches (10x+), zero-row actuals, row goals
        - Indexes: Missing index suggestions, key lookups, RID lookups, scan with residual predicates
        - Parallelism: Serial plan reasons, thread skew, ineffective parallelism, DOP reporting
        - Joins: Nested loop high executions, many-to-many merge join worktables
        - Filters: Late filter operators, function-wrapped predicates
        - Functions: Scalar UDF detection (T-SQL and CLR)
        - Parameters: Compiled vs runtime values, sniffing issue detection, local variables
        - Patterns: Leading wildcards, implicit conversions, OPTIMIZE FOR UNKNOWN, NOT IN with nullable columns
        - Compilation: High compile CPU, compile memory exceeded, early abort
        - Objects: Table variables, table-valued functions, CTE multiple references, spools

        Warnings have three severity levels: Critical, Warning, Info.

        ## Data Characteristics

        - Plans can be **estimated** (no runtime stats) or **actual** (with row counts, elapsed time, I/O stats)
        - Estimated plans show expected costs and row estimates only
        - Actual plans additionally show per-thread runtime data, elapsed times, logical/physical reads, wait stats
        - Memory grant analysis is only meaningful in actual plans (when GrantedKB > 0)
        - Wait stats are only present in actual plans captured with SET STATISTICS XML ON
        - Query Store plans are always estimated (plan cache snapshots)

        ## MCP Client Configuration

        For Claude Code, add to your MCP config:
        ```json
        {
          "mcpServers": {
            "performance-studio": {
              "type": "streamable-http",
              "url": "http://localhost:5152/"
            }
          }
        }
        ```

        ## Key Limitations

        - Plans must be loaded in the application before MCP tools can access them
        - Query Store tools require a saved connection with valid credentials
        - Plan XML in `get_plan_xml` is truncated at 500KB
        - The full operator tree in `analyze_plan` can be large for complex queries
        """;
}
