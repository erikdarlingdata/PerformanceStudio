# Repl Read-only Pilot

## Goal

Add a Repl 0.11.0-dev.181 command surface for loaded SQL Server plans while preserving the existing System.CommandLine CLI and its JSON contracts.

## Architecture

1. Put immutable plan-session models plus `IPlanCatalog` and its thread-safe in-memory implementation in `PlanViewer.Core`.
2. Put typed, renderer-free read operations (`open`, `list`, `summary`, `warnings`, `missing indexes`, `expensive operators`) in `PlanViewer.Core`.
3. Keep the Avalonia `PlanSessionManager` as a thin compatibility singleton over the Core catalog and make existing MCP tools delegate to Core operations without changing MCP names or JSON property names.
4. Add a Repl `IReplModule` in `PlanViewer.Cli`, pinned to `Repl`/`Repl.Mcp`/`Repl.Testing` 0.11.0-dev.181. Launch Repl for no args and for the new `plan` graph; dispatch all existing argument prefixes to System.CommandLine unchanged.
5. Return typed values from Repl handlers so text, JSON, tests, and MCP are renderer concerns rather than handler concerns.

## TDD slices

- Catalog registration, list, lookup, and removal.
- File-open success plus file-not-found, empty, and invalid-plan failures.
- Summary and severity-filtered warning DTOs.
- Expensive-operator ranking and `top` validation.
- Missing-index result shape.
- Repl one-shot graph and interactive session persistence, including paths with spaces and JSON output.
- Legacy CLI JSON contract characterization before Program routing changes.
- Existing MCP JSON shape characterization before delegation refactor.

## Verification

- `dotnet build PlanViewer.sln -c Release`
- `dotnet test PlanViewer.sln -c Release`
- Compare legacy JSON output before and after for a fixed `.sqlplan`.
- Run a Repl one-shot JSON smoke test.
- Run a redirected interactive smoke test covering open, summary, warnings, operators, missing indexes, list, and exit.
- Run `git diff --check` and report the exact Git provenance.


## Command graph

```text
open {path}                         # root convenience command; navigates to the plan
plan open {path}                    # canonical open command
plan list                           # process-local loaded plan sessions
plan {id} summary
plan {id} warnings [--severity Critical|Warning|Info]
plan {id} expensive-operators [--top 10]
plan {id} operators [--top 10]     # alias
plan {id} missing-indexes
```

Inside an interactive plan context, the `plan {id}` prefix is omitted. Handlers
return typed DTOs; operation results carry stable `JsonPropertyName` annotations, while
catalog summaries retain their existing property casing. Rendering is
selected by Repl (`--json`, text, YAML, or Markdown) rather than performed by the
handlers.

## Compatibility boundary

- `analyze`, `querystore`, `credential`, and their existing options remain on the
  original System.CommandLine root.
- The Repl graph is selected only for no arguments, `repl`, `plan`, `open`, or
  `mcp`.
- The existing `analyze --compact` JSON output is treated as a public contract and
  is checked byte-for-byte against a pre-change characterization fixture.
- The Avalonia MCP session manager now implements `IPlanCatalog`, and pilot MCP
  operations delegate to `PlanOperations` while retaining their existing MCP tool
  names and top-level JSON shapes.
- Sessions are process-local and read-only. Query Store mutations and credentials
  are intentionally outside this pilot.
