# planview

`planview` is the command-line part of [Performance Studio](https://github.com/erikdarlingdata/PerformanceStudio). It analyzes SQL Server execution plans and reports the warnings as JSON or text. It reads `.sqlplan` files, or it captures plans from a live server. It runs on Windows, Linux, and macOS with the .NET 10 runtime.

## Install

```bash
dotnet tool install -g PerformanceStudio.Cli
planview analyze --help
```

For a CI pipeline, install into a folder and pin the version:

```bash
dotnet tool install --tool-path ./tools PerformanceStudio.Cli --version <version>
./tools/planview analyze plan.sqlplan --compact
```

## Examples

```bash
planview analyze my_query.sqlplan
planview analyze my_query.sqlplan --output text --warnings-only
planview analyze ./queries/ --server sql2022 --database AdventureWorks --login sa --password-stdin --output-dir ./results/
planview repl
```

## Credentials

On Windows and macOS, `planview credential add` stores a login in the OS credential store. Linux has no credential store, so the command exits with an error there. Pass `--login` and `--password-stdin`, or put `PLANVIEW_LOGIN` and `PLANVIEW_PASSWORD` in a `.env` file.

The full guide, the analysis rules, and the desktop app are in the [repository README](https://github.com/erikdarlingdata/PerformanceStudio#readme).
