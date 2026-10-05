# Third-Party Notices

Performance Studio includes the following third-party open-source components. Each component is subject to the license terms specified below.

---

## vscode-mssql (Execution Plan Icons)

**Author**: Microsoft Corporation
**Repository**: https://github.com/microsoft/vscode-mssql
**License**: MIT License

Execution plan operator icons (PNG) from the vscode-mssql extension are used to render graphical execution plans. Icons are located in `src/PlanViewer.Core/Resources/PlanIcons/`.

### License Text

MIT License

Copyright (c) Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

Full license: https://github.com/microsoft/vscode-mssql/blob/main/LICENSE

---

## SqlFormatter

**Author**: Mads Kristensen
**Repository**: https://github.com/madskristensen/SqlFormatter
**License**: MIT (Apache-2.0 per repository; individual files carry MIT terms)

The `SqlFormattingService` in this project was inspired by and partially derived
from the SqlFormatter extension for Visual Studio by Mads Kristensen. It uses
`Microsoft.SqlServer.TransactSql.ScriptDom` for T-SQL parsing and formatting.

### License Text

MIT License

Copyright (c) Mads Kristensen

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

---

## gcf-dotnet (GCF Output for MCP Tool Results)

**Author**: Dayna Blackwell / Blackwell Systems
**Repository**: https://github.com/blackwell-systems/gcf-dotnet
**License**: Apache License 2.0

The `BlackwellSystems.Gcf` NuGet package encodes MCP tool results as GCF when `PLANVIEWER_OUTPUT_FORMAT=gcf` is set. The repository's LICENSE changed from MIT to Apache License 2.0 in v1.1.0. The metadata of the v1.1.0 NuGet package still declares MIT.

### NOTICE

```text
gcf-dotnet
Copyright 2026 Dayna Blackwell / Blackwell Systems

This product includes software developed by Dayna Blackwell / Blackwell Systems (https://github.com/blackwell-systems).
```

Full license: https://github.com/blackwell-systems/gcf-dotnet/blob/v1.1.0/LICENSE

---

## Acknowledgments

Performance Studio uses execution plan operator icons from **Microsoft's vscode-mssql extension**, which provides SQL Server tooling for Visual Studio Code. We are grateful for their commitment to open-source software.

---

*Last Updated: October 5, 2026*
