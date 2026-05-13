using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

public static partial class ShowPlanParser
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    public static ParsedPlan Parse(string xml)
    {
        var plan = new ParsedPlan { RawXml = xml };

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception ex)
        {
            plan.ParseError = ex.Message;
            return plan;
        }

        var root = doc.Root;
        if (root == null) return plan;

        plan.BuildVersion = root.Attribute("Version")?.Value;
        plan.Build = root.Attribute("Build")?.Value;
        plan.ClusteredMode = root.Attribute("ClusteredMode")?.Value is "true" or "1";

        // Standard path: ShowPlanXML → BatchSequence → Batch → Statements
        var batches = root.Descendants(Ns + "Batch");
        foreach (var batchEl in batches)
        {
            var batch = new PlanBatch();
            // A Batch can contain multiple <Statements> elements (e.g., DECLARE + SELECT).
            // Use Elements() to iterate all of them, not just the first.
            foreach (var statementsEl in batchEl.Elements(Ns + "Statements"))
            {
                foreach (var stmtEl in statementsEl.Elements())
                {
                    var stmts = ParseStatementAndChildren(stmtEl);
                    batch.Statements.AddRange(stmts);
                }
            }
            if (batch.Statements.Count > 0)
                plan.Batches.Add(batch);
        }

        // Fallback: some plan XML has StmtSimple directly under QueryPlan
        if (plan.Batches.Count == 0)
        {
            var batch = new PlanBatch();
            foreach (var stmtEl in root.Descendants(Ns + "StmtSimple"))
            {
                var stmt = ParseStatement(stmtEl);
                if (stmt != null)
                    batch.Statements.Add(stmt);
            }
            if (batch.Statements.Count > 0)
                plan.Batches.Add(batch);
        }

        ComputeOperatorCosts(plan);
        return plan;
    }

    /// <summary>
    /// Handles StmtSimple, StmtCond (IF/ELSE), and StmtCursor recursively.
    /// Returns a flat list of all parseable statements found.
    /// </summary>
    private static List<PlanStatement> ParseStatementAndChildren(XElement stmtEl)
    {
        var results = new List<PlanStatement>();
        var localName = stmtEl.Name.LocalName;

        if (localName == "StmtCond")
        {
            // IF/ELSE blocks — recurse into Condition, Then, Else
            var condEl = stmtEl.Element(Ns + "Condition");
            if (condEl != null)
            {
                foreach (var child in condEl.Elements())
                    results.AddRange(ParseStatementAndChildren(child));
            }

            var thenStmts = stmtEl.Element(Ns + "Then")?.Element(Ns + "Statements");
            if (thenStmts != null)
            {
                foreach (var child in thenStmts.Elements())
                    results.AddRange(ParseStatementAndChildren(child));
            }

            var elseStmts = stmtEl.Element(Ns + "Else")?.Element(Ns + "Statements");
            if (elseStmts != null)
            {
                foreach (var child in elseStmts.Elements())
                    results.AddRange(ParseStatementAndChildren(child));
            }
        }
        else if (localName == "StmtCursor")
        {
            // Cursor plans — parse each Operation's QueryPlan
            var cursorPlanEl = stmtEl.Element(Ns + "CursorPlan");
            if (cursorPlanEl != null)
            {
                var cursorName = cursorPlanEl.Attribute("CursorName")?.Value;
                var cursorActualType = cursorPlanEl.Attribute("CursorActualType")?.Value;
                var cursorRequestedType = cursorPlanEl.Attribute("CursorRequestedType")?.Value;
                var cursorConcurrency = cursorPlanEl.Attribute("CursorConcurrency")?.Value;
                var cursorForwardOnly = cursorPlanEl.Attribute("ForwardOnly")?.Value is "true" or "1";

                foreach (var opEl in cursorPlanEl.Elements(Ns + "Operation"))
                {
                    var opType = opEl.Attribute("OperationType")?.Value ?? "CursorOp";
                    var qpEl = opEl.Element(Ns + "QueryPlan");
                    if (qpEl == null) continue;

                    // Build a synthetic StmtSimple-like wrapper for ParseStatement
                    var relOpEl = qpEl.Element(Ns + "RelOp");
                    if (relOpEl == null) continue;

                    var stmt = ParseQueryPlanAsStatement(stmtEl, qpEl, relOpEl);
                    if (stmt != null)
                    {
                        // Override statement text with cursor context
                        if (string.IsNullOrEmpty(stmt.StatementText))
                            stmt.StatementText = $"Cursor: {cursorName} ({opType})";
                        stmt.CursorName = cursorName;
                        stmt.CursorActualType = cursorActualType;
                        stmt.CursorRequestedType = cursorRequestedType;
                        stmt.CursorConcurrency = cursorConcurrency;
                        stmt.CursorForwardOnly = cursorForwardOnly;
                        results.Add(stmt);
                    }
                }
            }
        }
        else
        {
            // StmtSimple or any other statement type
            var stmt = ParseStatement(stmtEl);
            if (stmt != null)
                results.Add(stmt);
        }

        return results;
    }

    private static PlanStatement? ParseStatement(XElement stmtEl)
    {
        var stmt = new PlanStatement
        {
            StatementText = stmtEl.Attribute("StatementText")?.Value ?? "",
            StatementType = stmtEl.Attribute("StatementType")?.Value ?? "",
            StatementSubTreeCost = ParseDouble(stmtEl.Attribute("StatementSubTreeCost")?.Value),
            StatementEstRows = ParseDouble(stmtEl.Attribute("StatementEstRows")?.Value)
        };

        // StmtUseDb: capture the Database attribute
        if (stmtEl.Name.LocalName == "StmtUseDb")
            stmt.StmtUseDatabaseName = stmtEl.Attribute("Database")?.Value;

        var queryPlanEl = stmtEl.Element(Ns + "QueryPlan");

        // XSD gap: Dispatcher/PSP (on StmtSimple, not inside QueryPlan)
        var dispatcherEl = stmtEl.Element(Ns + "Dispatcher");
        if (dispatcherEl != null)
        {
            stmt.Dispatcher = new DispatcherInfo();
            foreach (var pspEl in dispatcherEl.Elements(Ns + "ParameterSensitivePredicate"))
            {
                var psp = new ParameterSensitivePredicateInfo
                {
                    LowBoundary = ParseDouble(pspEl.Attribute("LowBoundary")?.Value),
                    HighBoundary = ParseDouble(pspEl.Attribute("HighBoundary")?.Value)
                };
                var predEl = pspEl.Element(Ns + "Predicate")?.Descendants(Ns + "ScalarOperator").FirstOrDefault();
                psp.PredicateText = predEl?.Attribute("ScalarString")?.Value;
                foreach (var statEl in pspEl.Elements(Ns + "StatisticsInfo"))
                {
                    psp.Statistics.Add(new OptimizerStatsUsageItem
                    {
                        StatisticsName = statEl.Attribute("Statistics")?.Value?.Replace("[", "").Replace("]", "") ?? "",
                        TableName = statEl.Attribute("Table")?.Value?.Replace("[", "").Replace("]", "") ?? "",
                        DatabaseName = statEl.Attribute("Database")?.Value?.Replace("[", "").Replace("]", ""),
                        SchemaName = statEl.Attribute("Schema")?.Value?.Replace("[", "").Replace("]", ""),
                        ModificationCount = ParseLong(statEl.Attribute("ModificationCount")?.Value),
                        SamplingPercent = ParseDouble(statEl.Attribute("SamplingPercent")?.Value),
                        LastUpdate = statEl.Attribute("LastUpdate")?.Value
                    });
                }
                stmt.Dispatcher.ParameterSensitivePredicates.Add(psp);
            }
            foreach (var oppEl in dispatcherEl.Elements(Ns + "OptionalParameterPredicate"))
            {
                var opp = new OptionalParameterPredicateInfo();
                var oppPredEl = oppEl.Element(Ns + "Predicate")?.Descendants(Ns + "ScalarOperator").FirstOrDefault();
                opp.PredicateText = oppPredEl?.Attribute("ScalarString")?.Value;
                stmt.Dispatcher.OptionalParameterPredicates.Add(opp);
            }
        }

        if (queryPlanEl == null)
        {
            // Statements with no QueryPlan (e.g., DECLARE/ASSIGN) still get a synthetic
            // root node so they appear in the statement tab list.
            var stmtType = stmt.StatementType.Length > 0
                ? stmt.StatementType.ToUpperInvariant()
                : "STATEMENT";
            stmt.RootNode = new PlanNode
            {
                NodeId = -1,
                PhysicalOp = stmtType,
                LogicalOp = stmtType,
                IconName = stmtType switch
                {
                    "ASSIGN" => "assign",
                    "DECLARE" => "declare",
                    _ => "language_construct_catch_all"
                }
            };
            return stmt;
        }

        ParseStmtAttributes(stmt, stmtEl);
        ParseQueryPlanElements(stmt, stmtEl, queryPlanEl);

        // Root RelOp — wrap in a synthetic statement-type node (SELECT, INSERT, etc.)
        var relOpEl = queryPlanEl.Element(Ns + "RelOp");
        if (relOpEl != null)
        {
            var opNode = ParseRelOp(relOpEl);
            var stmtType = stmt.StatementType.Length > 0
                ? stmt.StatementType.ToUpperInvariant()
                : "QUERY";

            var stmtNode = new PlanNode
            {
                NodeId = -1,
                PhysicalOp = stmtType,
                LogicalOp = stmtType,
                EstimatedTotalSubtreeCost = stmt.StatementSubTreeCost,
                EstimateRows = stmt.StatementEstRows,
                IconName = stmtType switch
                {
                    "SELECT" => "result",
                    "INSERT" => "insert",
                    "UPDATE" => "update",
                    "DELETE" => "delete",
                    _ => "language_construct_catch_all"
                }
            };
            opNode.Parent = stmtNode;
            stmtNode.Children.Add(opNode);
            stmt.RootNode = stmtNode;
        }

        // XSD gap: UDF sub-plans
        foreach (var udfEl in stmtEl.Elements(Ns + "UDF"))
        {
            var udfInfo = new FunctionPlanInfo
            {
                ProcName = udfEl.Attribute("ProcName")?.Value ?? "",
                IsNativelyCompiled = udfEl.Attribute("IsNativelyCompiled")?.Value is "true" or "1"
            };
            var udfStmts = udfEl.Element(Ns + "Statements");
            if (udfStmts != null)
            {
                foreach (var childStmt in udfStmts.Elements())
                {
                    var parsed = ParseStatementAndChildren(childStmt);
                    udfInfo.Statements.AddRange(parsed);
                }
            }
            stmt.UdfPlans.Add(udfInfo);
        }

        // XSD gap: StoredProc sub-plan
        var storedProcEl = stmtEl.Element(Ns + "StoredProc");
        if (storedProcEl != null)
        {
            var spInfo = new FunctionPlanInfo
            {
                ProcName = storedProcEl.Attribute("ProcName")?.Value ?? "",
                IsNativelyCompiled = storedProcEl.Attribute("IsNativelyCompiled")?.Value is "true" or "1"
            };
            var spStmts = storedProcEl.Element(Ns + "Statements");
            if (spStmts != null)
            {
                foreach (var childStmt in spStmts.Elements())
                {
                    var parsed = ParseStatementAndChildren(childStmt);
                    spInfo.Statements.AddRange(parsed);
                }
            }
            stmt.StoredProcPlan = spInfo;
        }

        return stmt;
    }

    /// <summary>
    /// Parse a QueryPlan element that comes from a cursor Operation (no parent StmtSimple attributes).
    /// </summary>
    private static PlanStatement? ParseQueryPlanAsStatement(XElement stmtEl, XElement queryPlanEl, XElement relOpEl)
    {
        var stmt = new PlanStatement
        {
            StatementText = stmtEl.Attribute("StatementText")?.Value ?? "",
            StatementType = stmtEl.Attribute("StatementType")?.Value ?? "SELECT",
            StatementSubTreeCost = ParseDouble(stmtEl.Attribute("StatementSubTreeCost")?.Value)
        };

        ParseStmtAttributes(stmt, stmtEl);
        ParseQueryPlanElements(stmt, stmtEl, queryPlanEl);

        var opNode = ParseRelOp(relOpEl);
        var stmtType = stmt.StatementType.Length > 0
            ? stmt.StatementType.ToUpperInvariant()
            : "QUERY";

        // Use subtree cost from RelOp if statement cost is 0
        if (stmt.StatementSubTreeCost <= 0)
            stmt.StatementSubTreeCost = opNode.EstimatedTotalSubtreeCost;

        var stmtNode = new PlanNode
        {
            NodeId = -1,
            PhysicalOp = stmtType,
            LogicalOp = stmtType,
            EstimatedTotalSubtreeCost = stmt.StatementSubTreeCost,
            EstimateRows = stmt.StatementEstRows,
            IconName = stmtType switch
            {
                "SELECT" => "result",
                "INSERT" => "insert",
                "UPDATE" => "update",
                "DELETE" => "delete",
                _ => "language_construct_catch_all"
            }
        };
        opNode.Parent = stmtNode;
        stmtNode.Children.Add(opNode);
        stmt.RootNode = stmtNode;

        return stmt;
    }

    /// <summary>
    /// Parse attributes from StmtSimple element.
    /// </summary>
    private static void ParseStmtAttributes(PlanStatement stmt, XElement stmtEl)
    {
        stmt.StatementOptmLevel = stmtEl.Attribute("StatementOptmLevel")?.Value;
        stmt.StatementOptmEarlyAbortReason = stmtEl.Attribute("StatementOptmEarlyAbortReason")?.Value;
        stmt.StatementParameterizationType = (int)ParseDouble(stmtEl.Attribute("StatementParameterizationType")?.Value);
        stmt.StatementSqlHandle = stmtEl.Attribute("StatementSqlHandle")?.Value;
        stmt.DatabaseContextSettingsId = ParseLong(stmtEl.Attribute("DatabaseContextSettingsId")?.Value);
        stmt.ParentObjectId = (int)ParseDouble(stmtEl.Attribute("ParentObjectId")?.Value);
        stmt.SecurityPolicyApplied = stmtEl.Attribute("SecurityPolicyApplied")?.Value is "true" or "1";
        stmt.BatchModeOnRowStoreUsed = stmtEl.Attribute("BatchModeOnRowStoreUsed")?.Value is "true" or "1";
        stmt.QueryHash = stmtEl.Attribute("QueryHash")?.Value;
        stmt.QueryPlanHash = stmtEl.Attribute("QueryPlanHash")?.Value;

        // Bug fix 1.3: CE version is on StmtSimple per XSD
        stmt.CardinalityEstimationModelVersion = (int)ParseDouble(stmtEl.Attribute("CardinalityEstimationModelVersion")?.Value);

        // Wave 3.6: Query Store hint attributes
        stmt.QueryStoreStatementHintId = (int)ParseDouble(stmtEl.Attribute("QueryStoreStatementHintId")?.Value);
        stmt.QueryStoreStatementHintText = stmtEl.Attribute("QueryStoreStatementHintText")?.Value;
        stmt.QueryStoreStatementHintSource = stmtEl.Attribute("QueryStoreStatementHintSource")?.Value;

        // XSD gap: Statement-level identifiers and handles
        stmt.StatementId = (int)ParseDouble(stmtEl.Attribute("StatementId")?.Value);
        stmt.StatementCompId = (int)ParseDouble(stmtEl.Attribute("StatementCompId")?.Value);
        stmt.TemplatePlanGuideDB = stmtEl.Attribute("TemplatePlanGuideDB")?.Value;
        stmt.TemplatePlanGuideName = stmtEl.Attribute("TemplatePlanGuideName")?.Value;
        stmt.ParameterizedPlanHandle = stmtEl.Attribute("ParameterizedPlanHandle")?.Value;
        stmt.BatchSqlHandle = stmtEl.Attribute("BatchSqlHandle")?.Value;
        stmt.ContainsLedgerTables = stmtEl.Attribute("ContainsLedgerTables")?.Value is "true" or "1";
        stmt.QueryCompilationReplay = (int)ParseDouble(stmtEl.Attribute("QueryCompilationReplay")?.Value);
    }

    /// <summary>
    /// Parse child elements of QueryPlan (memory grant, stats, parameters, etc.)
    /// </summary>
    private static void ParseQueryPlanElements(PlanStatement stmt, XElement stmtEl, XElement queryPlanEl)
    {
        // StatementSetOptions (child element of StmtSimple)
        var setOptsEl = stmtEl.Element(Ns + "StatementSetOptions");
        if (setOptsEl != null)
        {
            stmt.SetOptions = new SetOptionsInfo
            {
                AnsiNulls = setOptsEl.Attribute("ANSI_NULLS")?.Value is "true" or "1",
                AnsiPadding = setOptsEl.Attribute("ANSI_PADDING")?.Value is "true" or "1",
                AnsiWarnings = setOptsEl.Attribute("ANSI_WARNINGS")?.Value is "true" or "1",
                ArithAbort = setOptsEl.Attribute("ARITHABORT")?.Value is "true" or "1",
                ConcatNullYieldsNull = setOptsEl.Attribute("CONCAT_NULL_YIELDS_NULL")?.Value is "true" or "1",
                NumericRoundAbort = setOptsEl.Attribute("NUMERIC_ROUNDABORT")?.Value is "true" or "1",
                QuotedIdentifier = setOptsEl.Attribute("QUOTED_IDENTIFIER")?.Value is "true" or "1"
            };
        }

        // Memory grant info
        var memEl = queryPlanEl.Element(Ns + "MemoryGrantInfo");
        if (memEl != null)
        {
            stmt.MemoryGrant = new MemoryGrantInfo
            {
                SerialRequiredMemoryKB = ParseLong(memEl.Attribute("SerialRequiredMemory")?.Value),
                SerialDesiredMemoryKB = ParseLong(memEl.Attribute("SerialDesiredMemory")?.Value),
                RequiredMemoryKB = ParseLong(memEl.Attribute("RequiredMemory")?.Value),
                DesiredMemoryKB = ParseLong(memEl.Attribute("DesiredMemory")?.Value),
                RequestedMemoryKB = ParseLong(memEl.Attribute("RequestedMemory")?.Value),
                GrantedMemoryKB = ParseLong(memEl.Attribute("GrantedMemory")?.Value),
                MaxUsedMemoryKB = ParseLong(memEl.Attribute("MaxUsedMemory")?.Value),
                GrantWaitTimeMs = ParseLong(memEl.Attribute("GrantWaitTime")?.Value),
                LastRequestedMemoryKB = ParseLong(memEl.Attribute("LastRequestedMemory")?.Value),
                IsMemoryGrantFeedbackAdjusted = memEl.Attribute("IsMemoryGrantFeedbackAdjusted")?.Value
            };
        }

        // Statement-level metadata from QueryPlan attributes
        stmt.CachedPlanSizeKB = ParseLong(queryPlanEl.Attribute("CachedPlanSize")?.Value);
        stmt.DegreeOfParallelism = (int)ParseDouble(queryPlanEl.Attribute("DegreeOfParallelism")?.Value);
        stmt.NonParallelPlanReason = queryPlanEl.Attribute("NonParallelPlanReason")?.Value;
        stmt.RetrievedFromCache = stmtEl.Attribute("RetrievedFromCache")?.Value is "true" or "1";
        stmt.CompileTimeMs = ParseLong(queryPlanEl.Attribute("CompileTime")?.Value);
        stmt.CompileMemoryKB = ParseLong(queryPlanEl.Attribute("CompileMemory")?.Value);
        stmt.CompileCPUMs = ParseLong(queryPlanEl.Attribute("CompileCPU")?.Value);

        // Fallback: some plans have CE version on QueryPlan instead of StmtSimple
        if (stmt.CardinalityEstimationModelVersion == 0)
            stmt.CardinalityEstimationModelVersion = (int)ParseDouble(queryPlanEl.Attribute("CardinalityEstimationModelVersion")?.Value);

        // Wave 2.5: MaxQueryMemory
        stmt.MaxQueryMemoryKB = ParseLong(queryPlanEl.Attribute("MaxQueryMemory")?.Value);

        // Wave 3.1: EffectiveDOP + DOP feedback
        stmt.EffectiveDOP = (int)ParseDouble(queryPlanEl.Attribute("EffectiveDegreeOfParallelism")?.Value);
        stmt.DOPFeedbackAdjusted = queryPlanEl.Attribute("IsDOPFeedbackAdjusted")?.Value;

        // Wave 3.4: Plan Guide attributes
        stmt.PlanGuideDB = queryPlanEl.Attribute("PlanGuideDB")?.Value;
        stmt.PlanGuideName = queryPlanEl.Attribute("PlanGuideName")?.Value;
        stmt.UsePlan = queryPlanEl.Attribute("UsePlan")?.Value is "true" or "1";

        // Wave 3.5: ParameterizedText
        stmt.ParameterizedText = queryPlanEl.Element(Ns + "ParameterizedText")?.Value;

        // XSD gap: QueryPlan-level attributes
        stmt.ContainsInterleavedExecutionCandidates = queryPlanEl.Attribute("ContainsInterleavedExecutionCandidates")?.Value is "true" or "1";
        stmt.ContainsInlineScalarTsqlUdfs = queryPlanEl.Attribute("ContainsInlineScalarTsqlUdfs")?.Value is "true" or "1";
        stmt.QueryVariantID = (int)ParseDouble(queryPlanEl.Attribute("QueryVariantID")?.Value);
        stmt.DispatcherPlanHandle = queryPlanEl.Attribute("DispatcherPlanHandle")?.Value;
        stmt.ExclusiveProfileTimeActive = queryPlanEl.Attribute("ExclusiveProfileTimeActive")?.Value is "true" or "1";

        // QueryPlan-level MemoryGrant attribute (unsignedLong)
        stmt.QueryPlanMemoryGrantKB = ParseLong(queryPlanEl.Attribute("MemoryGrant")?.Value);

        // XSD gap: OptimizationReplay
        var optReplayEl = queryPlanEl.Element(Ns + "OptimizationReplay");
        if (optReplayEl != null)
            stmt.OptimizationReplayScript = optReplayEl.Attribute("Script")?.Value;

        // Missing indexes
        stmt.MissingIndexes = ParseMissingIndexes(queryPlanEl);

        // Wave 2.8: QueryPlan-level warnings
        var planWarningsEl = queryPlanEl.Element(Ns + "Warnings");
        if (planWarningsEl != null)
            stmt.PlanWarnings = ParseWarningsFromElement(planWarningsEl);

        // OptimizerHardwareDependentProperties
        var hwEl = queryPlanEl.Element(Ns + "OptimizerHardwareDependentProperties");
        if (hwEl != null)
        {
            stmt.HardwareProperties = new OptimizerHardwareInfo
            {
                EstimatedAvailableMemoryGrant = ParseLong(hwEl.Attribute("EstimatedAvailableMemoryGrant")?.Value),
                EstimatedPagesCached = ParseLong(hwEl.Attribute("EstimatedPagesCached")?.Value),
                EstimatedAvailableDOP = (int)ParseDouble(hwEl.Attribute("EstimatedAvailableDegreeOfParallelism")?.Value),
                MaxCompileMemory = ParseLong(hwEl.Attribute("MaxCompileMemory")?.Value)
            };
        }

        // OptimizerStatsUsage
        var statsUsageEl = queryPlanEl.Element(Ns + "OptimizerStatsUsage");
        if (statsUsageEl != null)
        {
            foreach (var statEl in statsUsageEl.Elements(Ns + "StatisticsInfo"))
            {
                stmt.StatsUsage.Add(new OptimizerStatsUsageItem
                {
                    StatisticsName = statEl.Attribute("Statistics")?.Value?.Replace("[", "").Replace("]", "") ?? "",
                    TableName = statEl.Attribute("Table")?.Value?.Replace("[", "").Replace("]", "") ?? "",
                    DatabaseName = statEl.Attribute("Database")?.Value?.Replace("[", "").Replace("]", ""),
                    SchemaName = statEl.Attribute("Schema")?.Value?.Replace("[", "").Replace("]", ""),
                    ModificationCount = ParseLong(statEl.Attribute("ModificationCount")?.Value),
                    SamplingPercent = ParseDouble(statEl.Attribute("SamplingPercent")?.Value),
                    LastUpdate = statEl.Attribute("LastUpdate")?.Value
                });
            }
        }

        // ThreadStat (actual plans)
        var threadStatEl = queryPlanEl.Element(Ns + "ThreadStat");
        if (threadStatEl != null)
        {
            stmt.ThreadStats = new ThreadStatInfo
            {
                Branches = (int)ParseDouble(threadStatEl.Attribute("Branches")?.Value),
                UsedThreads = (int)ParseDouble(threadStatEl.Attribute("UsedThreads")?.Value)
            };
            foreach (var trEl in threadStatEl.Elements(Ns + "ThreadReservation"))
            {
                stmt.ThreadStats.Reservations.Add(new ThreadReservation
                {
                    NodeId = (int)ParseDouble(trEl.Attribute("NodeId")?.Value),
                    ReservedThreads = (int)ParseDouble(trEl.Attribute("ReservedThreads")?.Value)
                });
            }
        }

        // ParameterList
        var paramListEl = queryPlanEl.Element(Ns + "ParameterList");
        if (paramListEl != null)
        {
            foreach (var paramEl in paramListEl.Elements(Ns + "ColumnReference"))
            {
                stmt.Parameters.Add(new PlanParameter
                {
                    Name = paramEl.Attribute("Column")?.Value ?? "",
                    DataType = paramEl.Attribute("ParameterDataType")?.Value ?? "",
                    CompiledValue = paramEl.Attribute("ParameterCompiledValue")?.Value,
                    RuntimeValue = paramEl.Attribute("ParameterRuntimeValue")?.Value
                });
            }
        }

        // WaitStats (actual plans)
        var waitStatsEl = queryPlanEl.Element(Ns + "WaitStats");
        if (waitStatsEl != null)
        {
            foreach (var waitEl in waitStatsEl.Elements(Ns + "Wait"))
            {
                stmt.WaitStats.Add(new WaitStatInfo
                {
                    WaitType = waitEl.Attribute("WaitType")?.Value ?? "",
                    WaitTimeMs = ParseLong(waitEl.Attribute("WaitTimeMs")?.Value),
                    WaitCount = ParseLong(waitEl.Attribute("WaitCount")?.Value)
                });
            }
        }

        // QueryTimeStats (actual plans)
        var queryTimeEl = queryPlanEl.Element(Ns + "QueryTimeStats");
        if (queryTimeEl != null)
        {
            stmt.QueryTimeStats = new QueryTimeInfo
            {
                CpuTimeMs = ParseLong(queryTimeEl.Attribute("CpuTime")?.Value),
                ElapsedTimeMs = ParseLong(queryTimeEl.Attribute("ElapsedTime")?.Value)
            };
            stmt.QueryUdfCpuTimeMs = ParseLong(queryTimeEl.Attribute("UdfCpuTime")?.Value);
            stmt.QueryUdfElapsedTimeMs = ParseLong(queryTimeEl.Attribute("UdfElapsedTime")?.Value);
        }

        // Wave 3.12: TraceFlags
        foreach (var traceFlagsEl in queryPlanEl.Elements(Ns + "TraceFlags"))
        {
            var isCompile = traceFlagsEl.Attribute("IsCompileTime")?.Value is "true" or "1";
            foreach (var tf in traceFlagsEl.Elements(Ns + "TraceFlag"))
            {
                stmt.TraceFlags.Add(new TraceFlagInfo
                {
                    Value = (int)ParseDouble(tf.Attribute("Value")?.Value),
                    Scope = tf.Attribute("Scope")?.Value ?? "",
                    IsCompileTime = isCompile
                });
            }
        }

        // Wave 3.13: IndexedViewInfo
        var ivInfoEl = queryPlanEl.Element(Ns + "IndexedViewInfo");
        if (ivInfoEl != null)
        {
            foreach (var objEl in ivInfoEl.Elements(Ns + "Object"))
            {
                var db = objEl.Attribute("Database")?.Value?.Replace("[", "").Replace("]", "");
                var schema = objEl.Attribute("Schema")?.Value?.Replace("[", "").Replace("]", "");
                var table = objEl.Attribute("Table")?.Value?.Replace("[", "").Replace("]", "");
                var index = objEl.Attribute("Index")?.Value?.Replace("[", "").Replace("]", "");
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(db)) parts.Add(db);
                if (!string.IsNullOrEmpty(schema)) parts.Add(schema);
                if (!string.IsNullOrEmpty(table)) parts.Add(table);
                if (!string.IsNullOrEmpty(index)) parts.Add(index);
                var name = string.Join(".", parts);
                if (!string.IsNullOrEmpty(name))
                    stmt.IndexedViews.Add(name);
            }
        }

        // XSD gap: CardinalityFeedback
        var ceFeedbackEl = queryPlanEl.Element(Ns + "CardinalityFeedback");
        if (ceFeedbackEl != null)
        {
            foreach (var entry in ceFeedbackEl.Elements(Ns + "Entry"))
            {
                stmt.CardinalityFeedback.Add(new CardinalityFeedbackEntry
                {
                    Key = ParseLong(entry.Attribute("Key")?.Value),
                    Value = ParseLong(entry.Attribute("Value")?.Value)
                });
            }
        }
    }


}
