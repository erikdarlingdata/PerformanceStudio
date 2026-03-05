# Contributing to Performance Studio

Thank you for your interest in contributing to Performance Studio! This guide will help you get started.

## Reporting Issues

- Use [GitHub Issues](https://github.com/erikdarlingdata/PerformanceStudio/issues) for bugs and feature requests
- Include the `.sqlplan` file (or a minimal reproduction) when reporting parser or analysis bugs
- Specify your OS and .NET version

## Development Setup

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Git

### Build and Test

```bash
git clone https://github.com/erikdarlingdata/PerformanceStudio.git
cd PerformanceStudio
dotnet build
dotnet test tests/PlanViewer.Core.Tests
```

### Run the GUI

```bash
dotnet run --project src/PlanViewer.App
```

### Run the CLI

```bash
dotnet run --project src/PlanViewer.Cli -- analyze --help
```

## Project Structure

```
PerformanceStudio/
├── src/
│   ├── PlanViewer.Core/       # Analysis engine (parser, rules, layout)
│   ├── PlanViewer.App/        # Avalonia desktop GUI
│   └── PlanViewer.Cli/        # CLI tool (planview command)
└── tests/
    └── PlanViewer.Core.Tests/ # xUnit tests with real .sqlplan fixtures
```

## Architecture

- **PlanViewer.Core** is the shared library. It contains the XML parser (`ShowPlanParser`), analysis rules (`PlanAnalyzer`), plan layout engine, text/JSON formatters, and all models. Both the GUI and CLI depend on it.
- **PlanViewer.App** is an Avalonia 11 desktop app using code-behind (no MVVM framework). It renders plan trees on a Canvas with the same operator icons as SSMS.
- **PlanViewer.Cli** is a System.CommandLine-based CLI tool that wraps Core for command-line use.

## Code Style

- File-scoped namespaces (`namespace Foo;`)
- Nullable enabled across all projects
- Code-behind pattern for UI (no MVVM, no ReactiveUI)
- No unnecessary abstractions — keep it simple and direct
- Tests use real `.sqlplan` XML fixtures, not mocks

## Adding Analysis Rules

Rules live in `PlanAnalyzer.cs`. Each rule:

1. Inspects `PlanNode` properties (statement-level rules) or individual operator nodes
2. Adds a `PlanWarning` with `WarningType`, `Message`, and `Severity` (Info, Warning, or Critical)
3. Has a corresponding test in `PlanAnalyzerTests.cs` with a minimal `.sqlplan` fixture

When adding a rule:
- Add the rule logic to `AnalyzeStatement()` or `AnalyzeNode()` in `PlanAnalyzer.cs`
- Create a minimal `.sqlplan` test fixture in `tests/PlanViewer.Core.Tests/Plans/`
- Add a test method in `PlanAnalyzerTests.cs`
- Ensure all existing tests still pass

## Pull Requests

1. Fork the repo and create a feature branch
2. Make your changes
3. Run `dotnet test` — all tests must pass
4. Run `dotnet build` — no warnings or errors
5. Open a PR with a clear description of what changed and why

## License

By contributing, you agree that your contributions will be licensed under the MIT License.
