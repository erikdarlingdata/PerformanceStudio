using System.IO;
using System.Text;
using System.Web;

namespace PlanViewer.Core.Output;

/// <summary>
/// Generates a self-contained HTML file from an AnalysisResult.
/// The output is a single .html file with embedded CSS that can be
/// opened in any browser offline — no server or internet required.
/// </summary>
public static class HtmlExporter
{
    public static string Export(AnalysisResult result, string textOutput)
    {
        var sb = new StringBuilder(32768);
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");

        WriteHead(sb, result);
        sb.AppendLine("<body>");
        WriteHeader(sb, result);
        sb.AppendLine("<main>");

        for (int i = 0; i < result.Statements.Count; i++)
        {
            WriteStatement(sb, result, result.Statements[i], i);
        }

        WriteTextAnalysis(sb, textOutput);
        sb.AppendLine("</main>");
        WriteFooter(sb);
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    private static void WriteHead(StringBuilder sb, AnalysisResult result)
    {
        var title = Encode($"Plan Analysis — {result.PlanSource}");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\" />");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\" />");
        sb.AppendLine($"<title>{title}</title>");
        sb.AppendLine("<style>");
        WriteCss(sb);
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
    }

    private static void WriteCss(StringBuilder sb)
    {
        sb.Append(@"
:root {
    --accent: #2eaef1;
    --bg: #ffffff;
    --bg-surface: #f5f5f5;
    --text: #333333;
    --text-secondary: #666666;
    --text-muted: #999999;
    --border: #e0e0e0;
    --critical: #d32f2f;
    --orange: #e67e22;
    --warning-color: #f39c12;
    --info: #2eaef1;
    --missing: #8e44ad;
    --card-runtime: #f0f4f8;
    --card-indexes: #fef8f0;
    --card-params: #f0f8f0;
    --card-waits: #f0f4fa;
    --card-runtime-border: #c8d8e8;
    --card-indexes-border: #e8d8c0;
    --card-params-border: #c0d8c0;
    --card-waits-border: #c0c8e0;
}
*, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
html, body {
    background: var(--bg); color: var(--text);
    font-family: 'Segoe UI', -apple-system, BlinkMacSystemFont, sans-serif;
    font-size: 14px; line-height: 1.5;
}
.export-header {
    padding: 0.6rem 2rem; background: #333333;
    border-bottom: 3px solid var(--accent); color: #fff;
}
.export-header-content {
    display: flex; align-items: center; gap: 1rem;
    max-width: 1200px; margin: 0 auto; flex-wrap: wrap;
}
.export-header h1 { font-size: 1rem; font-weight: 600; }
.plan-type {
    font-size: 0.75rem; padding: 0.15rem 0.5rem;
    border-radius: 3px; font-weight: 500;
}
.plan-type.actual { background: #e8f5e9; color: #2e7d32; }
.plan-type.estimated { background: #fff3e0; color: #e65100; }
.build-version { font-size: 0.8rem; color: #bbb; }
main { max-width: 1200px; margin: 0 auto; padding: 1rem 2rem; }

/* Statement */
.statement { margin-bottom: 2rem; }
.statement h2 {
    font-size: 1.1rem; font-weight: 600; color: var(--text);
    padding-bottom: 0.4rem; border-bottom: 2px solid var(--accent);
    margin-bottom: 0.75rem;
}

/* Insights grid */
.insights { display: grid; grid-template-columns: repeat(auto-fit, minmax(280px, 1fr)); gap: 0.75rem; margin-bottom: 0.75rem; }
.card { border-radius: 6px; border: 1px solid var(--border); overflow: hidden; }
.card h3, .card > summary {
    padding: 0.4rem 0.75rem; font-size: 0.8rem; font-weight: 500;
    border-bottom: 1px solid var(--border); display: flex; align-items: center; gap: 0.5rem;
    list-style: none; cursor: pointer;
}
.card > summary::-webkit-details-marker { display: none; }
.card > summary::before { content: ""\25B8""; font-size: 0.7rem; color: var(--text-muted); width: 0.7rem; }
details.card[open] > summary::before { content: ""\25BE""; }
.card.waits summary { color: #2a4365; }
.card-body { padding: 0.5rem 0.75rem; font-size: 0.8rem; }
.card.runtime { background: var(--card-runtime); border-color: var(--card-runtime-border); }
.card.runtime h3 { color: #2c5282; }
.card.indexes { background: var(--card-indexes); border-color: var(--card-indexes-border); }
.card.indexes h3 { color: #9c4221; }
.card.params { background: var(--card-params); border-color: var(--card-params-border); }
.card.params h3 { color: #276749; }
.card.waits { background: var(--card-waits); border-color: var(--card-waits-border); }
.card.waits h3 { color: #2a4365; }
.row { display: flex; justify-content: space-between; padding: 0.15rem 0; }
.label { color: var(--text-secondary); font-size: 0.75rem; }
.value { font-weight: 500; font-size: 0.8rem; }
.eff-good { color: #2e7d32; } .eff-warn { color: var(--orange); } .eff-bad { color: var(--critical); }
.card-count { font-size: 0.7rem; background: var(--bg-surface); padding: 0.1rem 0.4rem; border-radius: 8px; color: var(--text-secondary); }
.card-empty { color: var(--text-muted); font-style: italic; }

/* Missing indexes */
.mi-item { margin-bottom: 0.5rem; padding-bottom: 0.5rem; border-bottom: 1px solid var(--card-indexes-border); }
.mi-item:last-child { border-bottom: none; margin-bottom: 0; }
.mi-table { font-weight: 500; }
.mi-impact { font-size: 0.75rem; color: var(--text-secondary); }
.mi-impact-val { color: var(--orange); font-weight: 500; }
pre.mi-create {
    font-family: 'Cascadia Code', Consolas, monospace; font-size: 0.7rem;
    background: rgba(255,255,255,0.5); padding: 0.3rem 0.5rem;
    border-radius: 3px; margin-top: 0.25rem; white-space: pre-wrap; word-break: break-word;
}

/* Params table */
.params-table { width: 100%; border-collapse: collapse; font-size: 0.75rem; }
.params-table th {
    text-align: left; font-weight: 500; color: var(--text-secondary);
    padding: 0.2rem 0.4rem; border-bottom: 1px solid var(--card-params-border); font-size: 0.7rem;
}
.params-table td { padding: 0.2rem 0.4rem; }
.sniffing-row { background: #fdecea; }
.sniffing-val { color: var(--critical); font-weight: 600; }

/* Wait stats */
.wait-row { display: flex; align-items: center; gap: 0.5rem; padding: 0.15rem 0; }
.wait-type { flex: 0 0 auto; min-width: 120px; font-size: 0.75rem; }
.wait-bar-container { flex: 1; height: 10px; background: #e8ecf0; border-radius: 5px; overflow: hidden; }
.wait-bar { height: 100%; background: var(--accent); border-radius: 5px; }
.wait-ms { flex: 0 0 auto; font-size: 0.75rem; font-weight: 500; min-width: 60px; text-align: right; }

/* Warnings */
.warnings-section { margin-bottom: 0.75rem; }
.warnings-section h3 {
    font-size: 0.85rem; font-weight: 600; margin-bottom: 0.4rem;
    display: flex; align-items: center; gap: 0.5rem;
}
.warn-badge {
    font-size: 0.65rem; padding: 0.1rem 0.4rem; border-radius: 8px;
    color: #fff; font-weight: 600;
}
.warn-badge.critical { background: var(--critical); }
.warn-badge.warning { background: var(--warning-color); }
.warn-badge.info { background: var(--info); }
.warning-item {
    padding: 0.3rem 0.5rem; margin-bottom: 0.25rem;
    border-left: 3px solid var(--border); font-size: 0.8rem;
    display: flex; flex-wrap: wrap; gap: 0.3rem; align-items: baseline;
}
.warning-item.critical { border-left-color: var(--critical); background: #fdecea; }
.warning-item.warning { border-left-color: var(--warning-color); background: #fef8e8; }
.warning-item.info { border-left-color: var(--info); background: #e8f4fd; }
.sev { font-size: 0.7rem; font-weight: 600; padding: 0.05rem 0.3rem; border-radius: 3px; }
.sev-critical { color: var(--critical); }
.sev-warning { color: var(--warning-color); }
.sev-info { color: var(--info); }
.warn-op { font-size: 0.75rem; font-weight: 500; color: var(--text-secondary); }
.warn-type { font-size: 0.75rem; font-weight: 600; }
.warn-benefit { font-size: 0.7rem; font-weight: 600; color: var(--text-muted); padding: 0.05rem 0.3rem; border-radius: 3px; background: rgba(0,0,0,0.04); }
.warn-msg { font-size: 0.8rem; color: var(--text); flex-basis: 100%; }
.warn-legacy { font-size: 0.65rem; font-weight: 600; color: var(--text-muted); padding: 0.05rem 0.3rem; border-radius: 3px; background: rgba(0,0,0,0.08); text-transform: uppercase; letter-spacing: 0.05em; }
.warn-fix { font-size: 0.75rem; color: var(--text-secondary); font-style: italic; flex-basis: 100%; border-left: 2px solid var(--border); padding-left: 0.5rem; margin-top: 0.15rem; }
.spill-tag { font-size: 0.75rem; font-weight: 600; color: var(--orange); margin-left: 0.4rem; }

/* Query text */
details { margin-bottom: 0.75rem; }
details summary {
    cursor: pointer; font-size: 0.85rem; font-weight: 500;
    color: var(--text-secondary); padding: 0.3rem 0;
}
details summary:hover { color: var(--accent); }
pre.query-text, pre.text-output {
    font-family: 'Cascadia Code', Consolas, monospace; font-size: 0.8rem;
    background: var(--bg-surface); padding: 0.75rem; border-radius: 4px;
    border: 1px solid var(--border); white-space: pre-wrap; word-break: break-word;
    overflow-x: auto; max-height: 400px; overflow-y: auto;
}

/* Operator tree */
.op-tree { margin-bottom: 0.75rem; }
.op-tree h3 { font-size: 0.85rem; font-weight: 600; margin-bottom: 0.4rem; }
.op-node {
    padding: 0.25rem 0.4rem; margin: 0.15rem 0;
    border-left: 2px solid var(--border); font-size: 0.8rem;
}
.op-node.expensive { border-left-color: var(--critical); background: #fef0f0; }
.op-node.has-warnings { border-left-color: var(--warning-color); }
.op-name { font-weight: 500; }
.op-cost { color: var(--text-muted); font-size: 0.75rem; }
.op-rows { color: var(--text-secondary); font-size: 0.75rem; }
.op-object { color: var(--accent); font-size: 0.75rem; }
.op-time { font-size: 0.75rem; }
.op-warn-icon { color: var(--warning-color); }
.op-children { margin-left: 1.25rem; }

/* Footer */
.export-footer {
    text-align: center; padding: 1.5rem 2rem;
    border-top: 1px solid var(--border); margin-top: 2rem;
    font-size: 0.85rem; color: var(--text-muted);
}
.export-footer a { color: var(--accent); text-decoration: none; }

@media print {
    .export-header { background: #333 !important; -webkit-print-color-adjust: exact; print-color-adjust: exact; }
    details[open] > summary ~ * { display: block; }
    pre { max-height: none !important; overflow: visible !important; }
}
@media (max-width: 768px) {
    .insights { grid-template-columns: 1fr; }
    main { padding: 0.5rem; }
}
");
    }

    private static void WriteHeader(StringBuilder sb, AnalysisResult result)
    {
        var planType = result.Summary.HasActualStats ? "Actual Plan" : "Estimated Plan";
        var planClass = result.Summary.HasActualStats ? "actual" : "estimated";

        sb.AppendLine("<div class=\"export-header\">");
        sb.AppendLine("<div class=\"export-header-content\">");
        sb.AppendLine("<h1>Performance Studio &mdash; Plan Analysis</h1>");
        sb.AppendLine($"<span class=\"plan-type {planClass}\">{planType}</span>");
        if (result.SqlServerBuild != null)
            sb.AppendLine($"<span class=\"build-version\">{Encode(result.SqlServerBuild)}</span>");
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");
    }

    private static void WriteStatement(StringBuilder sb, AnalysisResult result, StatementResult stmt, int index)
    {
        sb.AppendLine("<div class=\"statement\">");

        if (result.Statements.Count > 1)
            sb.AppendLine($"<h2>Statement {index + 1}</h2>");

        // Insights grid
        sb.AppendLine("<div class=\"insights\">");
        WriteRuntimeCard(sb, stmt);
        WriteMissingIndexCard(sb, stmt);
        WriteParametersCard(sb, stmt);
        WriteWaitStatsCard(sb, stmt, result.Summary.HasActualStats);
        sb.AppendLine("</div>");

        // Warnings
        WriteWarnings(sb, stmt);

        // Query text
        sb.AppendLine("<details>");
        sb.AppendLine("<summary>Query Text</summary>");
        sb.AppendLine($"<pre class=\"query-text\">{Encode(stmt.StatementText)}</pre>");
        sb.AppendLine("</details>");

        // Operator tree
        if (stmt.OperatorTree != null)
        {
            sb.AppendLine("<div class=\"op-tree\">");
            sb.AppendLine("<h3>Operator Tree</h3>");
            WriteOperatorNode(sb, stmt.OperatorTree, stmt);
            sb.AppendLine("</div>");
        }

        sb.AppendLine("</div>");
    }

    private static void WriteRuntimeCard(StringBuilder sb, StatementResult stmt)
    {
        var isEstimated = stmt.QueryTime == null;
        var hasSpill = HasSpillInTree(stmt.OperatorTree);
        sb.AppendLine("<div class=\"card runtime\">");
        sb.AppendLine($"<h3>{(isEstimated ? "Predicted Runtime" : "Runtime")}</h3>");
        sb.AppendLine("<div class=\"card-body\">");

        // Order per Joe (#215 E11): Elapsed → CPU:Elapsed → DOP → CPU → Compile →
        // Memory → Used → Optimization → CE Model → Cost. Puts the important
        // measurements on top and groups related metrics together.
        if (stmt.QueryTime != null)
        {
            WriteRow(sb, "Elapsed", $"{stmt.QueryTime.ElapsedTimeMs:N0} ms");
            if (stmt.QueryTime.ElapsedTimeMs > 0)
            {
                var effectiveCpu = Math.Max(0, stmt.QueryTime.CpuTimeMs - stmt.QueryTime.ExternalWaitMs);
                var ratio = (double)effectiveCpu / stmt.QueryTime.ElapsedTimeMs;
                WriteRow(sb, "CPU:Elapsed", ratio.ToString("N2"));
            }
        }
        if (stmt.DegreeOfParallelism > 0)
            WriteRow(sb, "DOP", stmt.DegreeOfParallelism.ToString());
        if (stmt.NonParallelReason != null)
            WriteRow(sb, "Serial", Encode(stmt.NonParallelReason));
        if (stmt.QueryTime != null)
            WriteRow(sb, "CPU", $"{stmt.QueryTime.CpuTimeMs:N0} ms");
        if (stmt.CompileTimeMs > 0)
            WriteRow(sb, "Compile", $"{stmt.CompileTimeMs:N0} ms");
        if (stmt.MemoryGrant != null && stmt.MemoryGrant.GrantedKB > 0)
        {
            var pctUsed = (double)stmt.MemoryGrant.MaxUsedKB / stmt.MemoryGrant.GrantedKB * 100;
            var effClass = GetMemoryGrantColorClass(pctUsed, hasSpill);
            WriteRow(sb, "Memory", FormatKB(stmt.MemoryGrant.GrantedKB) + " granted");
            var spillTag = hasSpill ? " <span class=\"spill-tag\" title=\"Operators spilled to tempdb\">⚠ spill</span>" : "";
            sb.AppendLine($"<div class=\"row\"><span class=\"label\">Used</span><span class=\"value {effClass}\">{FormatKB(stmt.MemoryGrant.MaxUsedKB)} ({pctUsed:N0}%){spillTag}</span></div>");
        }
        else if (isEstimated && stmt.MemoryGrant != null && stmt.MemoryGrant.DesiredKB > 0)
        {
            // #215 E6: estimated plans — show the optimizer's pre-execution desired grant
            WriteRow(sb, "Memory (estimated)", FormatKB(stmt.MemoryGrant.DesiredKB) + " desired");
            if (stmt.MemoryGrant.SerialRequiredKB > 0 && stmt.MemoryGrant.SerialRequiredKB != stmt.MemoryGrant.DesiredKB)
                WriteRow(sb, "Serial required", FormatKB(stmt.MemoryGrant.SerialRequiredKB));
        }
        if (stmt.OptimizationLevel != null)
            WriteRow(sb, "Optimization", Encode(stmt.OptimizationLevel));
        if (stmt.CardinalityEstimationModel > 0)
            WriteRow(sb, "CE Model", stmt.CardinalityEstimationModel.ToString());
        WriteRow(sb, "Cost", stmt.EstimatedCost.ToString("N2"));
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");
    }

    /// <summary>
    /// Memory grant color tiers (#215 C1 + E8 + E9):
    /// - > 100% used: eff-bad (grant was too small, may have thrashed memory)
    /// - any operator spilled: eff-warn (grant was nominally enough but something spilled)
    /// - >= 40% used: eff-good (healthy utilization)
    /// - 20-39%: eff-warn (some over-grant)
    /// - < 20%: eff-bad (significant over-grant)
    /// </summary>
    private static string GetMemoryGrantColorClass(double pctUsed, bool hasSpill)
    {
        if (pctUsed > 100) return "eff-bad";
        if (hasSpill) return "eff-warn";
        if (pctUsed >= 40) return "eff-good";
        if (pctUsed >= 20) return "eff-warn";
        return "eff-bad";
    }

    private static bool HasSpillInTree(OperatorResult? node)
    {
        if (node == null) return false;
        foreach (var w in node.Warnings)
        {
            if (w.Type.EndsWith(" Spill", StringComparison.Ordinal))
                return true;
        }
        foreach (var child in node.Children)
            if (HasSpillInTree(child)) return true;
        return false;
    }

    private static void WriteMissingIndexCard(StringBuilder sb, StatementResult stmt)
    {
        sb.AppendLine($"<div class=\"card indexes\">");
        sb.AppendLine($"<h3>Missing Indexes <span class=\"card-count\">{stmt.MissingIndexes.Count}</span></h3>");
        sb.AppendLine("<div class=\"card-body\">");
        if (stmt.MissingIndexes.Count > 0)
        {
            foreach (var mi in stmt.MissingIndexes)
            {
                sb.AppendLine("<div class=\"mi-item\">");
                sb.AppendLine($"<div class=\"mi-table\">{Encode(mi.Table)}</div>");
                sb.AppendLine($"<div class=\"mi-impact\">Impact: <span class=\"mi-impact-val\">{mi.Impact:F0}%</span></div>");
                sb.AppendLine($"<pre class=\"mi-create\">{Encode(mi.CreateStatement)}</pre>");
                sb.AppendLine("</div>");
            }
        }
        else
        {
            sb.AppendLine("<div class=\"card-empty\">No missing index suggestions</div>");
        }
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");
    }

    private static void WriteParametersCard(StringBuilder sb, StatementResult stmt)
    {
        sb.AppendLine($"<div class=\"card params\">");
        sb.AppendLine($"<h3>Parameters <span class=\"card-count\">{stmt.Parameters.Count}</span></h3>");
        sb.AppendLine("<div class=\"card-body\">");
        if (stmt.Parameters.Count > 0)
        {
            var hasRuntime = stmt.Parameters.Any(p => p.RuntimeValue != null);
            sb.AppendLine("<table class=\"params-table\">");
            sb.AppendLine("<thead><tr><th>Name</th><th>Type</th><th>Compiled</th>");
            if (hasRuntime) sb.AppendLine("<th>Runtime</th>");
            sb.AppendLine("</tr></thead>");
            sb.AppendLine("<tbody>");
            foreach (var p in stmt.Parameters)
            {
                var rowClass = p.SniffingIssue ? " class=\"sniffing-row\"" : "";
                sb.AppendLine($"<tr{rowClass}>");
                sb.AppendLine($"<td>{Encode(p.Name)}</td>");
                sb.AppendLine($"<td>{Encode(p.DataType)}</td>");
                sb.AppendLine($"<td>{Encode(p.CompiledValue ?? "?")}</td>");
                if (hasRuntime)
                {
                    var valClass = p.SniffingIssue ? " class=\"sniffing-val\"" : "";
                    sb.AppendLine($"<td{valClass}>{Encode(p.RuntimeValue ?? "")}</td>");
                }
                sb.AppendLine("</tr>");
            }
            sb.AppendLine("</tbody></table>");
        }
        else
        {
            sb.AppendLine("<div class=\"card-empty\">No parameters</div>");
        }
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");
    }

    private static void WriteWaitStatsCard(StringBuilder sb, StatementResult stmt, bool hasActualStats)
    {
        // Collapsible (#215 E12): default-closed so improvement items aren't pushed below the fold.
        sb.AppendLine("<details class=\"card waits\">");
        sb.Append("<summary>Wait Stats");
        if (stmt.WaitStats.Count > 0)
            sb.Append($" <span class=\"card-count\">{stmt.WaitStats.Sum(w => w.WaitTimeMs):N0} ms</span>");
        sb.AppendLine("</summary>");
        sb.AppendLine("<div class=\"card-body\">");
        if (stmt.WaitStats.Count > 0)
        {
            // Build benefit lookup
            var benefitLookup = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var wb in stmt.WaitBenefits)
                benefitLookup[wb.WaitType] = wb.MaxBenefitPercent;

            var maxWait = stmt.WaitStats.Max(w => w.WaitTimeMs);
            foreach (var w in stmt.WaitStats.OrderByDescending(w => w.WaitTimeMs))
            {
                var barPct = maxWait > 0 ? (double)w.WaitTimeMs / maxWait * 100 : 0;
                var benefitTag = benefitLookup.TryGetValue(w.WaitType, out var pct)
                    ? $" <span class=\"warn-benefit\">up to {(pct >= 100 ? pct.ToString("N0") : pct.ToString("N1"))}%</span>"
                    : "";
                sb.AppendLine("<div class=\"wait-row\">");
                sb.AppendLine($"<span class=\"wait-type\">{Encode(w.WaitType)}</span>");
                sb.AppendLine($"<div class=\"wait-bar-container\"><div class=\"wait-bar\" style=\"width:{barPct:F0}%\"></div></div>");
                sb.AppendLine($"<span class=\"wait-ms\">{w.WaitTimeMs:N0} ms{benefitTag}</span>");
                sb.AppendLine("</div>");
            }
        }
        else
        {
            sb.AppendLine($"<div class=\"card-empty\">{(hasActualStats ? "No waits recorded" : "Estimated plan — no wait stats")}</div>");
        }
        sb.AppendLine("</div>");
        sb.AppendLine("</details>");
    }

    private static void WriteWarnings(StringBuilder sb, StatementResult stmt)
    {
        var allWarnings = new List<WarningResult>(stmt.Warnings);
        if (stmt.OperatorTree != null)
            CollectNodeWarnings(stmt.OperatorTree, allWarnings);

        if (allWarnings.Count == 0) return;

        var critCount = allWarnings.Count(w => w.Severity == "Critical");
        var warnCount = allWarnings.Count(w => w.Severity == "Warning");
        var infoCount = allWarnings.Count(w => w.Severity == "Info");

        sb.AppendLine("<div class=\"warnings-section\">");
        sb.Append("<h3>Warnings");
        if (critCount > 0) sb.Append($" <span class=\"warn-badge critical\">{critCount}</span>");
        if (warnCount > 0) sb.Append($" <span class=\"warn-badge warning\">{warnCount}</span>");
        if (infoCount > 0) sb.Append($" <span class=\"warn-badge info\">{infoCount}</span>");
        sb.AppendLine("</h3>");

        // Sort by benefit descending (nulls last), then severity, then type
        var sorted = allWarnings
            .OrderByDescending(w => w.MaxBenefitPercent ?? -1)
            .ThenBy(w => w.Severity switch { "Critical" => 0, "Warning" => 1, _ => 2 })
            .ThenBy(w => w.Type);

        foreach (var w in sorted)
        {
            var sevLower = w.Severity.ToLower();
            sb.AppendLine($"<div class=\"warning-item {sevLower}\">");
            sb.AppendLine($"<span class=\"sev sev-{sevLower}\">{Encode(w.Severity)}</span>");
            if (w.Operator != null)
                sb.AppendLine($"<span class=\"warn-op\">{Encode(w.Operator)}</span>");
            sb.AppendLine($"<span class=\"warn-type\">{Encode(w.Type)}</span>");
            if (w.IsLegacy)
                sb.AppendLine("<span class=\"warn-legacy\" title=\"Legacy rule — predates the benefit-scoring framework\">legacy</span>");
            if (w.MaxBenefitPercent.HasValue)
                sb.AppendLine($"<span class=\"warn-benefit\">up to {(w.MaxBenefitPercent.Value >= 100 ? w.MaxBenefitPercent.Value.ToString("N0") : w.MaxBenefitPercent.Value.ToString("N1"))}% benefit</span>");
            sb.AppendLine($"<span class=\"warn-msg\">{Encode(w.Message)}</span>");
            if (!string.IsNullOrEmpty(w.ActionableFix))
                sb.AppendLine($"<span class=\"warn-fix\">{Encode(w.ActionableFix)}</span>");
            sb.AppendLine("</div>");
        }
        sb.AppendLine("</div>");
    }

    private static void WriteOperatorNode(StringBuilder sb, OperatorResult node, StatementResult stmt)
    {
        var classes = "op-node";
        if (node.CostPercent >= 25) classes += " expensive";
        if (node.Warnings.Count > 0) classes += " has-warnings";

        sb.AppendLine($"<div class=\"{classes}\">");

        // Operator name + cost
        var opLabel = node.PhysicalOp;
        if (node.PhysicalOp == "Parallelism" && !string.IsNullOrEmpty(node.LogicalOp) && node.LogicalOp != "Parallelism")
            opLabel = $"Parallelism ({node.LogicalOp})";

        sb.Append($"<span class=\"op-name\">{Encode(opLabel)}</span>");
        sb.Append($" <span class=\"op-cost\">Cost: {node.CostPercent}%</span>");

        if (node.Warnings.Count > 0)
            sb.Append($" <span class=\"op-warn-icon\">&#x26A0;</span>");

        // Rows
        if (node.ActualRows.HasValue)
        {
            var est = node.EstimatedRows;
            var ratio = est > 0 ? (double)node.ActualRows.Value / est : 0;
            var accuracy = est > 0 ? $" ({ratio * 100:F0}%)" : "";
            sb.Append($" <span class=\"op-rows\">{node.ActualRows.Value:N0} of {est:N0} rows{accuracy}</span>");
        }
        else
        {
            sb.Append($" <span class=\"op-rows\">{node.EstimatedRows:N0} est. rows</span>");
        }

        // Timing (actual plans)
        if (node.ActualElapsedMs.HasValue && node.ActualElapsedMs > 0)
            sb.Append($" <span class=\"op-time\">{node.ActualElapsedMs.Value:N0}ms</span>");

        // Object
        if (!string.IsNullOrEmpty(node.ObjectName))
            sb.Append($" <span class=\"op-object\">{Encode(node.ObjectName)}</span>");

        sb.AppendLine();

        // Children
        if (node.Children.Count > 0)
        {
            sb.AppendLine("<div class=\"op-children\">");
            foreach (var child in node.Children)
                WriteOperatorNode(sb, child, stmt);
            sb.AppendLine("</div>");
        }

        sb.AppendLine("</div>");
    }

    private static void WriteTextAnalysis(StringBuilder sb, string textOutput)
    {
        sb.AppendLine("<details open>");
        sb.AppendLine("<summary>Full Text Analysis</summary>");
        sb.AppendLine($"<pre class=\"text-output\">{Encode(textOutput)}</pre>");
        sb.AppendLine("</details>");
    }

    private static void WriteFooter(StringBuilder sb)
    {
        var year = DateTime.Now.Year;
        var date = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        sb.AppendLine("<div class=\"export-footer\">");
        sb.AppendLine($"<div>Exported {date} &mdash; <a href=\"https://github.com/erikdarlingdata/PerformanceStudio\">Performance Studio</a></div>");
        sb.AppendLine($"<div>Copyright &copy; 2019-{year} Darling Data</div>");
        sb.AppendLine("</div>");
    }

    private static void WriteRow(StringBuilder sb, string label, string value)
    {
        sb.AppendLine($"<div class=\"row\"><span class=\"label\">{label}</span><span class=\"value\">{value}</span></div>");
    }

    private static void CollectNodeWarnings(OperatorResult node, List<WarningResult> warnings)
    {
        warnings.AddRange(node.Warnings);
        foreach (var child in node.Children)
            CollectNodeWarnings(child, warnings);
    }

    private static string FormatKB(long kb)
    {
        if (kb < 1024) return $"{kb:N0} KB";
        if (kb < 1024 * 1024) return $"{kb / 1024.0:N1} MB";
        return $"{kb / (1024.0 * 1024.0):N2} GB";
    }

    private static string Encode(string text) => HttpUtility.HtmlEncode(text);
}
