# Repl Read-only Pilot

## Goal

Add a Repl 0.11.0 command surface for loaded SQL Server plans while preserving the existing System.CommandLine CLI and its JSON contracts.

Read-only describes the analyzed plans and external systems: the pilot never writes a plan file, database, Query Store, or credential. `open` and `close` do mutate the process-local session catalog and are therefore not annotated as read-only MCP operations.

## Architecture

1. Put detached analysis snapshots plus `IPlanCatalog` and its thread-safe in-memory implementation in `PlanViewer.Core`. REPL sessions retain the analyzed projection rather than the mutable parser graph; desktop sessions keep their UI graph and expose a separately captured MCP projection.
2. Put typed, renderer-free operations (`open`, `close`, `list`, `summary`, `warnings`, `missing indexes`, `expensive operators`) and the shared parse/analyze/score pipeline in `PlanViewer.Core`.
3. Keep the Avalonia `PlanSessionManager` as a thin compatibility singleton over the Core catalog. Existing App MCP tools delegate to Core operations using the already-resolved session snapshot, avoiding a second catalog lookup while preserving their historical output behavior.
4. Add a Repl `IReplModule` in `PlanViewer.Cli`, pinned to the stable `Repl`/`Repl.Mcp`/`Repl.Testing` 0.11.0 release. Launch Repl only for explicit `repl`, `plan`, `open`, or `mcp` prefixes; zero arguments and existing prefixes remain on System.CommandLine.
5. Return typed values from Repl handlers so text, JSON, tests, and MCP are renderer concerns rather than handler concerns.

## Security and resource boundaries

Local CLI/Repl commands may open any accessible `.sqlplan` file, matching normal command-line file semantics. The generated MCP server applies a stricter policy:

- `plan_open` rejects lexically outside paths before probing them, opens an allowed candidate once, validates its kernel-resolved handle against resolved MCP roots, and parses that same handle. Containment is ordinal on Linux and case-insensitive on Windows and macOS to match their default filesystems and client URI casing; per-directory/per-volume case-sensitive configurations on Windows or macOS are outside this pilot's scope. NTFS alternate-data-stream paths are rejected.
- The server launch directory is a fallback root only when native roots are unsupported and no soft roots are configured. Advertised-but-empty, invalid, unavailable, or unresponsive roots deny access; native roots negotiation has a 5-second server-side deadline.
- MCP errors do not disclose absolute paths outside the allowed roots.
- Only an explicit allow-list of plan tools is exported; automatic read-only resource promotion is disabled.
- Files are read through a hard 16 MiB streaming ceiling even if they grow during loading. A forward-only XML preflight prohibits DTDs and rejects depth over 512, more than 10,000 structurally recognized statements/cursor operations, 100,000 operators, 250,000 total elements, or 1,000,000 total attributes before `XDocument` and the parser graph are materialized; the parsed graph is checked again before analysis. Streaming SHA-256 values bind the preflight and decoded passes to identical bytes, rejecting in-place content changes before materialization.
- The limit of 2 concurrent opens is shared by every `PlanOperations` facade over the same catalog; excess calls wait asynchronously, honor request cancellation, and do not resolve paths or acquire files until admitted. Admission is coordinated atomically across those facades, with at most 32 sessions and a 64 MiB aggregate retained-analysis estimate. The estimate is reserved after streaming preflight and conservatively accounts for UTF-16 source text, statements (2 KiB each), operators (4 KiB each), XML elements (256 bytes each), and attributes (128 bytes each); `plan_close` releases it.
- Warning responses return at most 500 items, missing-index responses at most 100 suggestions, and operator queries at most 100 items. The query paths count or rank by bounded top-K storage instead of materializing an additional full-tree result list. The generated CLI/MCP pilot admits at most 4 full-tree query traversals concurrently per catalog and rejects excess work; the existing desktop App MCP host remains exempt from that new cap. Result metadata reports truncation, and plan-controlled text fields are bounded before serialization.
- `plan_close` provides explicit eviction and releases the retained-memory reservation. Session labels are capped at 48 characters and use a fresh 12-hex-character opaque suffix; active-ID collisions are retried.
- Streaming preflight, direct bounded text decoding, per-statement/per-operator analysis, scoring, mapping, registration, and Repl query traversals honor cancellation. XML decoding reads the authorized stream directly with a pooled buffer rather than retaining a second byte-for-byte copy.

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
- Legacy `analyze` keeps its existing input behavior. The pilot intentionally accepts only `.sqlplan` in `open`/`plan_open`; this narrow local-file boundary is not retrofitted onto historical commands.
- The Repl graph is selected only for explicit `repl`, `plan`, `open`, or `mcp` prefixes; zero arguments preserve legacy routing.
- A committed process test runs `analyze --compact` and compares the exact stdout SHA-256 with the pre-change baseline (`0c609fed8e250d9366eb9a6cd5eaf40b661ee30d7ba2546bd7726960592e9d87`).
- The Avalonia MCP session manager implements `IPlanCatalog`; committed App-side tests pin the historical warning scope, invalid-severity message, bare `object_name`, numeric default metrics, and snapshot behavior.
- The desktop MCP host constructs shared operations with `AnalyzerConfig.Default`, so an unrelated malformed `planview.config.json` cannot prevent that server from starting.
- Sessions are process-local. Query Store mutations and credentials are intentionally outside this pilot.

## Dependency status

The pilot is pinned to the stable `0.11.0` packages. The CLI, persistent-session, and real MCP tests exercise the Repl APIs used by this integration.

The Repl repository is MIT licensed. The `0.11.0` NuGet packages still omit license metadata; correcting `PackageLicenseExpression` requires a subsequent Repl package publication and remains an upstream packaging follow-up rather than being represented as fixed by this repository.
