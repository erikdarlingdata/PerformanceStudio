using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

public static partial class ShowPlanParser
{
    private static List<MissingIndex> ParseMissingIndexes(XElement queryPlanEl)
    {
        var result = new List<MissingIndex>();
        var missingIndexesEl = queryPlanEl.Element(Ns + "MissingIndexes");
        if (missingIndexesEl == null) return result;

        foreach (var groupEl in missingIndexesEl.Elements(Ns + "MissingIndexGroup"))
        {
            var impact = ParseDouble(groupEl.Attribute("Impact")?.Value);
            foreach (var indexEl in groupEl.Elements(Ns + "MissingIndex"))
            {
                var mi = new MissingIndex
                {
                    Database = indexEl.Attribute("Database")?.Value?.Replace("[", "").Replace("]", "") ?? "",
                    Schema = indexEl.Attribute("Schema")?.Value?.Replace("[", "").Replace("]", "") ?? "",
                    Table = CleanTempTableName(indexEl.Attribute("Table")?.Value?.Replace("[", "").Replace("]", "") ?? ""),
                    Impact = impact
                };

                foreach (var colGroup in indexEl.Elements(Ns + "ColumnGroup"))
                {
                    var usage = colGroup.Attribute("Usage")?.Value ?? "";
                    var cols = colGroup.Elements(Ns + "Column")
                        .Select(c => c.Attribute("Name")?.Value?.Replace("[", "").Replace("]", "") ?? "")
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToList();

                    switch (usage)
                    {
                        case "EQUALITY": mi.EqualityColumns = cols; break;
                        case "INEQUALITY": mi.InequalityColumns = cols; break;
                        case "INCLUDE": mi.IncludeColumns = cols; break;
                    }
                }

                var keyCols = mi.EqualityColumns.Concat(mi.InequalityColumns).ToList();
                if (keyCols.Count > 0)
                {
                    var quotedKeyCols = keyCols.Select(c => $"[{c}]");
                    var create = $"CREATE NONCLUSTERED INDEX [{mi.Table}_{string.Join("_", keyCols.Take(3))}]\nON [{mi.Schema}].[{mi.Table}] ({string.Join(", ", quotedKeyCols)})";
                    if (mi.IncludeColumns.Count > 0)
                    {
                        var quotedIncludes = mi.IncludeColumns.Select(c => $"[{c}]");
                        create += $"\nINCLUDE ({string.Join(", ", quotedIncludes)})";
                    }
                    create += ";";
                    mi.CreateStatement = create;
                }

                result.Add(mi);
            }
        }
        return result;
    }

    /// <summary>
    /// Parse warnings from a parent element that contains a &lt;Warnings&gt; child (e.g. RelOp).
    /// </summary>
    private static List<PlanWarning> ParseWarnings(XElement parentEl)
    {
        var warningsEl = parentEl.Element(Ns + "Warnings");
        if (warningsEl == null) return new List<PlanWarning>();
        return ParseWarningsFromElement(warningsEl);
    }

    /// <summary>
    /// Parse warnings directly from a &lt;Warnings&gt; element.
    /// </summary>
    private static List<PlanWarning> ParseWarningsFromElement(XElement warningsEl)
    {
        var result = new List<PlanWarning>();

        // No join predicate
        if (warningsEl.Attribute("NoJoinPredicate")?.Value is "true" or "1")
        {
            result.Add(new PlanWarning
            {
                WarningType = "No Join Predicate",
                Message = "This join triggered a no join predicate warning, which is worth checking on, but is often misleading. The optimizer may have removed a redundant predicate after simplification.",
                Severity = PlanWarningSeverity.Warning
            });
        }

        if (warningsEl.Attribute("SpatialGuess")?.Value is "true" or "1")
        {
            result.Add(new PlanWarning
            {
                WarningType = "Spatial Guess",
                Message = "Spatial index selectivity was guessed",
                Severity = PlanWarningSeverity.Info
            });
        }

        if (warningsEl.Attribute("UnmatchedIndexes")?.Value is "true" or "1")
        {
            // Parse child UnmatchedIndexes detail if present
            var unmatchedMsg = "Indexes could not be matched due to parameterization";
            var unmatchedEl = warningsEl.Element(Ns + "UnmatchedIndexes");
            if (unmatchedEl != null)
            {
                var unmatchedDetails = new List<string>();
                foreach (var paramEl in unmatchedEl.Elements(Ns + "Parameterization"))
                {
                    var db = paramEl.Attribute("Database")?.Value?.Replace("[", "").Replace("]", "");
                    var schema = paramEl.Attribute("Schema")?.Value?.Replace("[", "").Replace("]", "");
                    var table = paramEl.Attribute("Table")?.Value?.Replace("[", "").Replace("]", "");
                    var index = paramEl.Attribute("Index")?.Value?.Replace("[", "").Replace("]", "");
                    var parts = new List<string>();
                    if (!string.IsNullOrEmpty(db)) parts.Add(db);
                    if (!string.IsNullOrEmpty(schema)) parts.Add(schema);
                    if (!string.IsNullOrEmpty(table)) parts.Add(table);
                    if (!string.IsNullOrEmpty(index)) parts.Add(index);
                    if (parts.Count > 0)
                        unmatchedDetails.Add(string.Join(".", parts));
                }
                if (unmatchedDetails.Count > 0)
                    unmatchedMsg += ": " + string.Join(", ", unmatchedDetails);
            }
            result.Add(new PlanWarning
            {
                WarningType = "Unmatched Indexes",
                Message = unmatchedMsg,
                Severity = PlanWarningSeverity.Warning
            });
        }

        if (warningsEl.Attribute("FullUpdateForOnlineIndexBuild")?.Value is "true" or "1")
        {
            result.Add(new PlanWarning
            {
                WarningType = "Full Update for Online Index Build",
                Message = "Full update required for online index build operation",
                Severity = PlanWarningSeverity.Info
            });
        }

        // Spill to TempDb — collect SpillToTempDb level/thread info first
        var spillLevel = "";
        var spillThreads = "";
        var spillToTempDbEl = warningsEl.Element(Ns + "SpillToTempDb");
        if (spillToTempDbEl != null)
        {
            spillLevel = spillToTempDbEl.Attribute("SpillLevel")?.Value ?? "?";
            spillThreads = spillToTempDbEl.Attribute("SpilledThreadCount")?.Value ?? "?";
        }

        // Sort spill details — merged with SpillToTempDb level/thread info
        foreach (var sortSpillEl in warningsEl.Elements(Ns + "SortSpillDetails"))
        {
            var granted = ParseLong(sortSpillEl.Attribute("GrantedMemoryKb")?.Value);
            var used = ParseLong(sortSpillEl.Attribute("UsedMemoryKb")?.Value);
            var writes = ParseLong(sortSpillEl.Attribute("WritesToTempDb")?.Value);
            var reads = ParseLong(sortSpillEl.Attribute("ReadsFromTempDb")?.Value);
            var prefix = spillLevel != "" ? $"Sort spill level {spillLevel}, {spillThreads} thread(s)" : "Sort spill";
            result.Add(new PlanWarning
            {
                WarningType = "Sort Spill",
                Message = $"{prefix} — Granted: {granted:N0} KB, Used: {used:N0} KB, Writes: {writes:N0}, Reads: {reads:N0}",
                Severity = PlanWarningSeverity.Warning,
                SpillDetails = new SpillDetail
                {
                    SpillType = "Sort",
                    GrantedMemoryKB = granted,
                    UsedMemoryKB = used,
                    WritesToTempDb = writes,
                    ReadsFromTempDb = reads
                }
            });
        }

        // Hash spill details — merged with SpillToTempDb level/thread info
        foreach (var hashSpillEl in warningsEl.Elements(Ns + "HashSpillDetails"))
        {
            var granted = ParseLong(hashSpillEl.Attribute("GrantedMemoryKb")?.Value);
            var used = ParseLong(hashSpillEl.Attribute("UsedMemoryKb")?.Value);
            var writes = ParseLong(hashSpillEl.Attribute("WritesToTempDb")?.Value);
            var reads = ParseLong(hashSpillEl.Attribute("ReadsFromTempDb")?.Value);
            var prefix = spillLevel != "" ? $"Hash spill level {spillLevel}, {spillThreads} thread(s)" : "Hash spill";
            result.Add(new PlanWarning
            {
                WarningType = "Hash Spill",
                Message = $"{prefix} — Granted: {granted:N0} KB, Used: {used:N0} KB, Writes: {writes:N0}, Reads: {reads:N0}",
                Severity = PlanWarningSeverity.Warning,
                SpillDetails = new SpillDetail
                {
                    SpillType = "Hash",
                    GrantedMemoryKB = granted,
                    UsedMemoryKB = used,
                    WritesToTempDb = writes,
                    ReadsFromTempDb = reads
                }
            });
        }

        // Standalone SpillToTempDb — only emit if no Sort/Hash detail element consumed it
        if (spillToTempDbEl != null &&
            !warningsEl.Elements(Ns + "SortSpillDetails").Any() &&
            !warningsEl.Elements(Ns + "HashSpillDetails").Any())
        {
            var msg = $"Spill level {spillLevel}, {spillThreads} thread(s)";
            var grantedKB = ParseLong(spillToTempDbEl.Attribute("GrantedMemoryKB")?.Value);
            var usedKB = ParseLong(spillToTempDbEl.Attribute("UsedMemoryKB")?.Value);
            var writes = ParseLong(spillToTempDbEl.Attribute("WritesToTempDb")?.Value);
            var reads = ParseLong(spillToTempDbEl.Attribute("ReadsFromTempDb")?.Value);
            if (grantedKB > 0 || writes > 0)
            {
                msg += $" — Granted: {grantedKB:N0} KB, Used: {usedKB:N0} KB";
                if (writes > 0) msg += $", Writes: {writes:N0}";
                if (reads > 0) msg += $", Reads: {reads:N0}";
            }

            result.Add(new PlanWarning
            {
                WarningType = "Spill to TempDb",
                Message = msg,
                Severity = PlanWarningSeverity.Warning
            });
        }

        // Exchange spill details
        foreach (var exchSpillEl in warningsEl.Elements(Ns + "ExchangeSpillDetails"))
        {
            result.Add(new PlanWarning
            {
                WarningType = "Exchange Spill",
                Message = $"Exchange spill — {ParseLong(exchSpillEl.Attribute("WritesToTempDb")?.Value):N0} writes to TempDB. The parallel exchange operator ran out of memory buffers and spilled rows to disk. This typically means the memory grant was too small for the data volume flowing through this exchange.",
                Severity = PlanWarningSeverity.Warning,
                SpillDetails = new SpillDetail
                {
                    SpillType = "Exchange",
                    WritesToTempDb = ParseLong(exchSpillEl.Attribute("WritesToTempDb")?.Value)
                }
            });
        }

        // SpillOccurred
        var spillOccurredEl = warningsEl.Element(Ns + "SpillOccurred");
        if (spillOccurredEl != null)
        {
            result.Add(new PlanWarning
            {
                WarningType = "Spill Occurred",
                Message = "Spill occurred during execution (from last query plan stats)",
                Severity = PlanWarningSeverity.Warning
            });
        }

        // Memory grant warning (from plan XML) — gate at 1 GB to avoid noise on small grants
        // All values are in KB, consistent with MemoryGrantInfo element
        var memWarnEl = warningsEl.Element(Ns + "MemoryGrantWarning");
        if (memWarnEl != null)
        {
            var kind = memWarnEl.Attribute("GrantWarningKind")?.Value ?? "Unknown";
            var requested = ParseLong(memWarnEl.Attribute("RequestedMemory")?.Value);
            var granted = ParseLong(memWarnEl.Attribute("GrantedMemory")?.Value);
            var maxUsed = ParseLong(memWarnEl.Attribute("MaxUsedMemory")?.Value);
            if (granted >= 1048576) // 1 GB in KB
            {
                var grantedMB = granted / 1024.0;
                var usedMB = maxUsed / 1024.0;
                result.Add(new PlanWarning
                {
                    WarningType = "Memory Grant",
                    Message = $"{kind}: Granted {grantedMB:N0} MB, Used {usedMB:N0} MB",
                    Severity = PlanWarningSeverity.Warning
                });
            }
        }

        // Implicit conversions
        foreach (var convertEl in warningsEl.Elements(Ns + "PlanAffectingConvert"))
        {
            var issue = convertEl.Attribute("ConvertIssue")?.Value ?? "Unknown";
            var expr = convertEl.Attribute("Expression")?.Value ?? "";
            result.Add(new PlanWarning
            {
                WarningType = "Implicit Conversion",
                Message = $"{issue}: {expr}",
                Severity = issue.Contains("Cardinality") ? PlanWarningSeverity.Warning : PlanWarningSeverity.Critical
            });
        }

        // Columns with no statistics
        var noStatsEl = warningsEl.Element(Ns + "ColumnsWithNoStatistics");
        if (noStatsEl != null)
        {
            var cols = noStatsEl.Elements(Ns + "ColumnReference")
                .Select(c => c.Attribute("Column")?.Value ?? "")
                .Where(s => !string.IsNullOrEmpty(s));
            result.Add(new PlanWarning
            {
                WarningType = "Missing Statistics",
                Message = $"No statistics on: {string.Join(", ", cols)}",
                Severity = PlanWarningSeverity.Warning
            });
        }

        // Wave 2.3: Columns with stale statistics
        var staleStatsEl = warningsEl.Element(Ns + "ColumnsWithStaleStatistics");
        if (staleStatsEl != null)
        {
            var cols = staleStatsEl.Elements(Ns + "ColumnReference")
                .Select(c => c.Attribute("Column")?.Value ?? "")
                .Where(s => !string.IsNullOrEmpty(s));
            result.Add(new PlanWarning
            {
                WarningType = "Stale Statistics",
                Message = $"Stale statistics on: {string.Join(", ", cols)}",
                Severity = PlanWarningSeverity.Warning
            });
        }

        // Wait warnings
        foreach (var waitEl in warningsEl.Elements(Ns + "Wait"))
        {
            result.Add(new PlanWarning
            {
                WarningType = "Wait",
                Message = $"{waitEl.Attribute("WaitType")?.Value}: {waitEl.Attribute("WaitTime")?.Value}ms",
                Severity = PlanWarningSeverity.Info
            });
        }

        return result;
    }
}
