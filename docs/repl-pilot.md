# Repl Read-only Pilot

## Goal

Add a Repl 0.11.0 command surface for loaded SQL Server plans while preserving the existing System.CommandLine CLI and its JSON contracts.

Read-only describes the analyzed plans and external systems: the pilot never writes a plan file, database, Query Store, or credential. `open` and `close` do mutate the process-local session catalog and are therefore not annotated as read-only MCP operations.

## Architecture

1. Put immutable plan-session models plus `IPlanCatalog` and its thread-safe in-memory implementation in `PlanViewer.Core`.
2. Put typed, renderer-free operations (`open`, `close`, `list`, `summary`, `warnings`, `missing indexes`, `expensive operators`) in `PlanViewer.Core`.
3. Keep the Avalonia `PlanSessionManager` as a thin compatibility singleton over the Core catalog. Existing App MCP tools delegate to Core operations using the already-resolved session snapshot, avoiding a second catalog lookup while preserving their historical output behavior.
4. Add a Repl `IReplModule` in `PlanViewer.Cli`, pinned to the stable `Repl`/`Repl.Mcp`/`Repl.Testing` 0.11.0 release. Launch Repl for no args and for the new `plan` graph; dispatch all existing argument prefixes to System.CommandLine unchanged.
5. Return typed values from Repl handlers so text, JSON, tests, and MCP are renderer concerns rather than handler concerns.

## Security and resource boundaries

Local CLI/Repl commands may open any accessible `.sqlplan` file, matching normal command-line file semantics. The generated MCP server applies a stricter policy:

- `plan_open` rejects lexically outside paths before probing them, opens an allowed candidate once, validates its kernel-resolved handle against canonical MCP roots, and parses that same handle.
- The server launch directory is a fallback root only when native roots are unsupported and no soft roots are configured. Advertised-but-empty, invalid, or unavailable roots deny access.
- MCP errors do not disclose absolute paths outside the allowed roots.
- Only an explicit allow-list of plan tools is exported; automatic read-only resource promotion is disabled.
- Files are read through a hard 16 MiB streaming ceiling even if they grow during loading; plans are limited to 10,000 statements and 100,000 operators including recursively retained UDF/stored-procedure subplans, concurrent opens to 2, and the process catalog to 32 sessions.
- `plan_close` provides explicit eviction. Catalog registration and the 32-session ceiling are enforced atomically, and closed session IDs are never reused.
- Loading honors cancellation before and between parse, analysis, scoring, and registration stages.

## TDD slices

- Catalog registration, bounded concurrent allocation, list, lookup, and removal.
- File-open success plus extension, missing, empty, malformed, oversized, and complexity failures.
- Summary and severity-filtered warning DTOs.
- Expensive-operator ranking and `top` validation.
- Missing-index result shape.
- Repl one-shot graph, close/eviction, and interactive session persistence.
- App MCP behavior characterization for warning scope, invalid severity handling, bare object names, numeric actual-stat defaults, and single-snapshot lookup.
- Real stdio MCP coverage for the exact tool allow-list, annotations, empty resource list, roots confinement, advertised empty-root denial, symbolic-link escape rejection, and an allowed open/summary round trip.
- Direct path-policy coverage pins same-handle validation/reading across a pathname swap where the platform permits the swap.

## Verification

- `dotnet build PlanViewer.sln -c Release`
- `dotnet test PlanViewer.sln -c Release`
- Compare legacy compact JSON output byte-for-byte against `upstream/dev` for a fixed `.sqlplan`.
- Run Repl one-shot and persistent-session smoke tests.
- Start the generated MCP server over stdio with a real `McpClient` and a bounded timeout.
- Run `git diff --check`, package-vulnerability inspection, and added-line secret/injection scans.

## Command graph

```text
open {path}                         # local convenience command; navigates to the plan
plan open {path}                    # canonical open command
plan list                           # process-local loaded plan sessions
plan {id} summary
plan {id} warnings [--severity Critical|Warning|Info]
plan {id} expensive-operators [--top 10]
plan {id} operators [--top 10]     # local alias
plan {id} missing-indexes
plan {id} close                     # evict the in-memory session
```

Inside an interactive plan context, the `plan {id}` prefix is omitted. Handlers return typed DTOs; operation results carry stable `JsonPropertyName` annotations, while catalog summaries retain their existing property casing. Rendering is selected by Repl (`--json`, text, YAML, or Markdown) rather than performed by handlers.

The generated MCP server identifies itself as `planview`. It exports the canonical commands only; the root `open` convenience command and `operators` alias remain local.

## Compatibility boundary

- `analyze`, `query-store`, `credential`, and their existing options remain on the original System.CommandLine root.
- The Repl graph is selected only for no arguments, `repl`, `plan`, `open`, or `mcp`.
- The existing `analyze --compact` JSON output is checked manually byte-for-byte against a pre-change baseline.
- The Avalonia MCP session manager implements `IPlanCatalog`; committed App-side tests pin the historical warning scope, invalid-severity message, bare `object_name`, numeric default metrics, and snapshot behavior.
- The desktop MCP host constructs shared operations with `AnalyzerConfig.Default`, so an unrelated malformed `planview.config.json` cannot prevent that server from starting.
- Sessions are process-local. Query Store mutations and credentials are intentionally outside this pilot.

## Dependency status

The pilot is pinned to the stable `0.11.0` packages. The CLI, persistent-session, and real MCP tests exercise the Repl APIs used by this integration.

The Repl repository is MIT licensed. The `0.11.0` NuGet packages still omit license metadata; correcting `PackageLicenseExpression` requires a subsequent Repl package publication and remains an upstream packaging follow-up rather than being represented as fixed by this repository.
