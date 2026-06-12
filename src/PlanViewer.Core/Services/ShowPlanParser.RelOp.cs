using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

public static partial class ShowPlanParser
{
    private static PlanNode ParseRelOp(XElement relOpEl, int depth = 0)
    {
        if (depth > MaxParseDepth)
            throw new InvalidOperationException("Plan operator nesting exceeds the supported depth limit.");

        var node = new PlanNode
        {
            NodeId = (int)ParseDouble(relOpEl.Attribute("NodeId")?.Value),
            PhysicalOp = relOpEl.Attribute("PhysicalOp")?.Value ?? "",
            LogicalOp = relOpEl.Attribute("LogicalOp")?.Value ?? "",
            EstimatedTotalSubtreeCost = ParseDouble(relOpEl.Attribute("EstimatedTotalSubtreeCost")?.Value),
            EstimateRows = ParseDouble(relOpEl.Attribute("EstimateRows")?.Value),
            EstimateIO = ParseDouble(relOpEl.Attribute("EstimateIO")?.Value),
            EstimateCPU = ParseDouble(relOpEl.Attribute("EstimateCPU")?.Value),
            EstimateRebinds = ParseDouble(relOpEl.Attribute("EstimateRebinds")?.Value),
            EstimateRewinds = ParseDouble(relOpEl.Attribute("EstimateRewinds")?.Value),
            EstimatedRowSize = (int)ParseDouble(relOpEl.Attribute("AvgRowSize")?.Value),
            Parallel = relOpEl.Attribute("Parallel")?.Value is "true" or "1",
            Partitioned = relOpEl.Attribute("Partitioned")?.Value is "true" or "1",
            ExecutionMode = relOpEl.Attribute("EstimatedExecutionMode")?.Value,
            IsAdaptive = relOpEl.Attribute("IsAdaptive")?.Value is "true" or "1",
            AdaptiveThresholdRows = ParseDouble(relOpEl.Attribute("AdaptiveThresholdRows")?.Value),
            EstimatedJoinType = relOpEl.Attribute("EstimatedJoinType")?.Value,
            // Wave 3.14: Estimated DOP per operator
            EstimatedDOP = (int)ParseDouble(relOpEl.Attribute("EstimatedAvailableDegreeOfParallelism")?.Value),
            // XSD gap: RelOp-level metadata
            GroupExecuted = relOpEl.Attribute("GroupExecuted")?.Value is "true" or "1",
            RemoteDataAccess = relOpEl.Attribute("RemoteDataAccess")?.Value is "true" or "1",
            OptimizedHalloweenProtectionUsed = relOpEl.Attribute("OptimizedHalloweenProtectionUsed")?.Value is "true" or "1",
            StatsCollectionId = ParseLong(relOpEl.Attribute("StatsCollectionId")?.Value)
        };

        // Spool operators: prepend Eager/Lazy from LogicalOp to PhysicalOp
        // XML has PhysicalOp="Index Spool" but LogicalOp="Eager Spool" — show "Eager Index Spool"
        if (node.PhysicalOp.EndsWith("Spool", StringComparison.OrdinalIgnoreCase)
            && node.LogicalOp.StartsWith("Eager", StringComparison.OrdinalIgnoreCase))
        {
            node.PhysicalOp = "Eager " + node.PhysicalOp;
        }
        else if (node.PhysicalOp.EndsWith("Spool", StringComparison.OrdinalIgnoreCase)
            && node.LogicalOp.StartsWith("Lazy", StringComparison.OrdinalIgnoreCase))
        {
            node.PhysicalOp = "Lazy " + node.PhysicalOp;
        }


        // Icon mapping is deferred until after StorageType is parsed below,
        // so columnstore scans (which surface as Clustered/Index Scan with
        // Storage="ColumnStore") can be routed to the columnstore icon.

        // Handle operator-specific element
        var physicalOpEl = GetOperatorElement(relOpEl);
        if (physicalOpEl != null)
        {
            // Top N Sort — XML element is <TopSort> but PhysicalOp is "Sort"
            if (physicalOpEl.Name.LocalName == "TopSort")
                node.LogicalOp = "Top N Sort";

            // Object reference (table/index name) — scoped to stop at child RelOps
            var objEl = ScopedDescendants(physicalOpEl, Ns + "Object").FirstOrDefault();
            if (objEl != null)
            {
                var db = objEl.Attribute("Database")?.Value?.Replace("[", "").Replace("]", "");
                var schema = objEl.Attribute("Schema")?.Value?.Replace("[", "").Replace("]", "");
                var table = CleanTempTableName(objEl.Attribute("Table")?.Value?.Replace("[", "").Replace("]", "") ?? "");
                var index = objEl.Attribute("Index")?.Value?.Replace("[", "").Replace("]", "");

                node.DatabaseName = db;
                node.IndexName = index;

                var shortParts = new List<string>();
                if (!string.IsNullOrEmpty(schema)) shortParts.Add(schema);
                if (!string.IsNullOrEmpty(table)) shortParts.Add(table);
                node.ObjectName = shortParts.Count > 0 ? string.Join(".", shortParts) : null;

                var fullParts = new List<string>();
                if (!string.IsNullOrEmpty(db)) fullParts.Add(db);
                if (!string.IsNullOrEmpty(schema)) fullParts.Add(schema);
                if (!string.IsNullOrEmpty(table)) fullParts.Add(table);
                var fullName = string.Join(".", fullParts);
                if (!string.IsNullOrEmpty(index))
                    fullName += $".{index}";
                node.FullObjectName = !string.IsNullOrEmpty(fullName) ? fullName : null;

                node.StorageType = objEl.Attribute("Storage")?.Value;
                node.ServerName = objEl.Attribute("Server")?.Value?.Replace("[", "").Replace("]", "");
                node.ObjectAlias = objEl.Attribute("Alias")?.Value?.Replace("[", "").Replace("]", "");
                node.IndexKind = objEl.Attribute("IndexKind")?.Value;
                node.FilteredIndex = objEl.Attribute("Filtered")?.Value is "true" or "1";
                node.TableReferenceId = (int)ParseDouble(objEl.Attribute("TableReferenceId")?.Value);
            }

            // Nonclustered indexes maintained by modification operators (Update/SimpleUpdate)
            var opName = physicalOpEl.Name.LocalName;
            if (opName is "Update" or "SimpleUpdate" or "CreateIndex")
            {
                var ncObjects = ScopedDescendants(physicalOpEl, Ns + "Object")
                    .Where(o => string.Equals(o.Attribute("IndexKind")?.Value, "NonClustered", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                node.NonClusteredIndexCount = ncObjects.Count;
                foreach (var ncObj in ncObjects)
                {
                    var ixName = ncObj.Attribute("Index")?.Value?.Replace("[", "").Replace("]", "");
                    if (!string.IsNullOrEmpty(ixName))
                        node.NonClusteredIndexNames.Add(ixName);
                }
            }

            // Hash keys for hash match operators
            var hashKeysProbeEl = physicalOpEl.Element(Ns + "HashKeysProbe");
            if (hashKeysProbeEl != null)
            {
                var cols = hashKeysProbeEl.Elements(Ns + "ColumnReference")
                    .Select(c => FormatColumnRef(c))
                    .Where(s => !string.IsNullOrEmpty(s));
                node.HashKeysProbe = string.Join(", ", cols);
            }
            var hashKeysBuildEl = physicalOpEl.Element(Ns + "HashKeysBuild");
            if (hashKeysBuildEl != null)
            {
                var cols = hashKeysBuildEl.Elements(Ns + "ColumnReference")
                    .Select(c => FormatColumnRef(c))
                    .Where(s => !string.IsNullOrEmpty(s));
                node.HashKeysBuild = string.Join(", ", cols);
            }

            // Ordered attribute
            node.Ordered = physicalOpEl.Attribute("Ordered")?.Value == "true" || physicalOpEl.Attribute("Ordered")?.Value == "1";

            // Seek predicates — scoped to stop at child RelOps
            var seekPreds = ScopedDescendants(physicalOpEl, Ns + "SeekPredicateNew")
                .Concat(ScopedDescendants(physicalOpEl, Ns + "SeekPredicate"));
            var seekParts = new List<string>();
            foreach (var sp in seekPreds)
            {
                foreach (var seekKeys in sp.Elements(Ns + "SeekKeys"))
                {
                    // Each SeekKeys has Prefix, StartRange, EndRange with ScanType
                    foreach (var range in seekKeys.Elements())
                    {
                        var scanType = range.Attribute("ScanType")?.Value;
                        var cols = range.Element(Ns + "RangeColumns")?
                            .Elements(Ns + "ColumnReference")
                            .Select(FormatColumnRef)
                            .ToList();
                        var exprs = range.Element(Ns + "RangeExpressions")?
                            .Elements(Ns + "ScalarOperator")
                            .Select(so => so.Attribute("ScalarString")?.Value ?? "?")
                            .ToList();

                        if (cols != null && exprs != null)
                        {
                            var op = scanType switch
                            {
                                "EQ" => "=", "GT" => ">", "GE" => ">=",
                                "LT" => "<", "LE" => "<=", _ => scanType ?? "="
                            };
                            for (int ci = 0; ci < cols.Count && ci < exprs.Count; ci++)
                                seekParts.Add($"{cols[ci]} {op} {exprs[ci]}");
                        }
                    }
                }
            }
            if (seekParts.Count > 0)
                node.SeekPredicates = string.Join(", ", seekParts);

            // GuessedSelectivity — check if optimizer guessed selectivity on predicates
            if (ScopedDescendants(physicalOpEl, Ns + "GuessedSelectivity").Any())
                node.GuessedSelectivity = true;

            // Residual predicate
            var predEl = physicalOpEl.Elements(Ns + "Predicate").FirstOrDefault();
            if (predEl != null)
            {
                var scalarOp = predEl.Descendants(Ns + "ScalarOperator").FirstOrDefault();
                node.Predicate = scalarOp?.Attribute("ScalarString")?.Value;
            }

            // Partitioning type (for parallelism operators)
            node.PartitioningType = physicalOpEl.Attribute("PartitioningType")?.Value;

            // Build/Probe residuals (Hash Match)
            var buildResEl = physicalOpEl.Element(Ns + "BuildResidual");
            if (buildResEl != null)
            {
                var so = buildResEl.Descendants(Ns + "ScalarOperator").FirstOrDefault();
                node.BuildResidual = so?.Attribute("ScalarString")?.Value;
            }
            var probeResEl = physicalOpEl.Element(Ns + "ProbeResidual");
            if (probeResEl != null)
            {
                var so = probeResEl.Descendants(Ns + "ScalarOperator").FirstOrDefault();
                node.ProbeResidual = so?.Attribute("ScalarString")?.Value;
            }

            // Wave 2.1/2.2: Merge Residual + PassThru (Merge Join + Nested Loops)
            var residualEl = physicalOpEl.Element(Ns + "Residual");
            if (residualEl != null)
            {
                var so = residualEl.Descendants(Ns + "ScalarOperator").FirstOrDefault();
                node.MergeResidual = so?.Attribute("ScalarString")?.Value;
            }
            var passThruEl = physicalOpEl.Element(Ns + "PassThru");
            if (passThruEl != null)
            {
                var so = passThruEl.Descendants(Ns + "ScalarOperator").FirstOrDefault();
                node.PassThru = so?.Attribute("ScalarString")?.Value;
            }

            // OrderBy columns (Sort operator)
            var orderByEl = physicalOpEl.Element(Ns + "OrderBy");
            if (orderByEl != null)
            {
                var obParts = orderByEl.Elements(Ns + "OrderByColumn")
                    .Select(obc =>
                    {
                        var ascending = obc.Attribute("Ascending")?.Value != "false";
                        var colRef = obc.Element(Ns + "ColumnReference");
                        var name = colRef != null ? FormatColumnRef(colRef) : "";
                        return string.IsNullOrEmpty(name) ? "" : $"{name} {(ascending ? "ASC" : "DESC")}";
                    })
                    .Where(s => !string.IsNullOrEmpty(s));
                var obStr = string.Join(", ", obParts);
                if (!string.IsNullOrEmpty(obStr))
                    node.OrderBy = obStr;
            }

            // OuterReferences (Nested Loops)
            var outerRefsEl = physicalOpEl.Element(Ns + "OuterReferences");
            if (outerRefsEl != null)
            {
                var refs = outerRefsEl.Elements(Ns + "ColumnReference")
                    .Select(c => FormatColumnRef(c))
                    .Where(s => !string.IsNullOrEmpty(s));
                var refsStr = string.Join(", ", refs);
                if (!string.IsNullOrEmpty(refsStr))
                    node.OuterReferences = refsStr;
            }

            // Inner/Outer side join columns (Merge Join)
            node.InnerSideJoinColumns = ParseColumnList(physicalOpEl, "InnerSideJoinColumns");
            node.OuterSideJoinColumns = ParseColumnList(physicalOpEl, "OuterSideJoinColumns");

            // GroupBy columns (Hash/Stream Aggregate)
            node.GroupBy = ParseColumnList(physicalOpEl, "GroupBy");

            // Partition columns (Parallelism)
            node.PartitionColumns = ParseColumnList(physicalOpEl, "PartitionColumns");

            // Wave 2.6: Parallelism HashKeys
            node.HashKeys = ParseColumnList(physicalOpEl, "HashKeys");

            // Segment column
            var segColEl = physicalOpEl.Element(Ns + "SegmentColumn")?.Element(Ns + "ColumnReference");
            if (segColEl != null)
                node.SegmentColumn = FormatColumnRef(segColEl);

            // Defined values (Compute Scalar)
            var definedValsEl = physicalOpEl.Element(Ns + "DefinedValues");
            if (definedValsEl != null)
            {
                var dvParts = new List<string>();
                foreach (var dvEl in definedValsEl.Elements(Ns + "DefinedValue"))
                {
                    var colRef = dvEl.Element(Ns + "ColumnReference");
                    var scalarOp = dvEl.Element(Ns + "ScalarOperator");
                    var colName = colRef != null ? FormatColumnRef(colRef) : "";
                    var expr = scalarOp?.Attribute("ScalarString")?.Value ?? "";
                    if (!string.IsNullOrEmpty(colName) && !string.IsNullOrEmpty(expr))
                        dvParts.Add($"{colName} = {expr}");
                    else if (!string.IsNullOrEmpty(expr))
                        dvParts.Add(expr);
                    else if (!string.IsNullOrEmpty(colName))
                        dvParts.Add(colName);
                }
                if (dvParts.Count > 0)
                    node.DefinedValues = string.Join("; ", dvParts);
            }

            // IndexScan / TableScan properties
            node.ScanDirection = physicalOpEl.Attribute("ScanDirection")?.Value;
            node.ForcedIndex = physicalOpEl.Attribute("ForcedIndex")?.Value is "true" or "1";
            node.ForceScan = physicalOpEl.Attribute("ForceScan")?.Value is "true" or "1";
            node.ForceSeek = physicalOpEl.Attribute("ForceSeek")?.Value is "true" or "1";
            node.NoExpandHint = physicalOpEl.Attribute("NoExpandHint")?.Value is "true" or "1";
            node.Lookup = physicalOpEl.Attribute("Lookup")?.Value is "true" or "1";
            node.DynamicSeek = physicalOpEl.Attribute("DynamicSeek")?.Value is "true" or "1";

            // Override PhysicalOp, LogicalOp, and icon when Lookup=true.
            // SQL Server's XML emits PhysicalOp="Clustered Index Seek" with <IndexScan Lookup="1">
            // rather than "Key Lookup (Clustered)" — correct the label here so all display
            // paths (node card, tooltip, properties panel) show the right operator name.
            if (node.Lookup)
            {
                var isHeap = node.IndexKind?.Equals("Heap", StringComparison.OrdinalIgnoreCase) == true
                             || node.PhysicalOp.StartsWith("RID Lookup", StringComparison.OrdinalIgnoreCase);
                node.PhysicalOp = isHeap ? "RID Lookup (Heap)" : "Key Lookup (Clustered)";
                node.LogicalOp  = isHeap ? "RID Lookup" : "Key Lookup";
                node.IconName   = isHeap ? "rid_lookup" : "bookmark_lookup";
            }

            // Table cardinality and rows to be read (on <RelOp> per XSD)
            node.TableCardinality = ParseDouble(relOpEl.Attribute("TableCardinality")?.Value);
            node.EstimatedRowsRead = ParseDouble(relOpEl.Attribute("EstimatedRowsRead")?.Value);
            node.EstimateRowsWithoutRowGoal = ParseDouble(relOpEl.Attribute("EstimateRowsWithoutRowGoal")?.Value);
            if (node.EstimatedRowsRead == 0)
                node.EstimatedRowsRead = node.EstimateRowsWithoutRowGoal;

            // TOP operator properties
            var topExprEl = physicalOpEl.Element(Ns + "TopExpression")?.Descendants(Ns + "ScalarOperator").FirstOrDefault();
            if (topExprEl != null)
                node.TopExpression = topExprEl.Attribute("ScalarString")?.Value;
            node.IsPercent = physicalOpEl.Attribute("IsPercent")?.Value is "true" or "1";
            node.WithTies = physicalOpEl.Attribute("WithTies")?.Value is "true" or "1";

            // Wave 2.7: Top OffsetExpression, RowCount, Rows
            var offsetEl = physicalOpEl.Element(Ns + "OffsetExpression")?.Descendants(Ns + "ScalarOperator").FirstOrDefault();
            if (offsetEl != null)
                node.OffsetExpression = offsetEl.Attribute("ScalarString")?.Value;
            node.RowCount = physicalOpEl.Attribute("RowCount")?.Value is "true" or "1";
            node.TopRows = (int)ParseDouble(physicalOpEl.Attribute("Rows")?.Value);

            // Sort properties
            node.SortDistinct = physicalOpEl.Attribute("Distinct")?.Value is "true" or "1";

            // Filter properties
            node.StartupExpression = physicalOpEl.Attribute("StartupExpression")?.Value is "true" or "1";

            // Nested Loops properties
            node.NLOptimized = physicalOpEl.Attribute("Optimized")?.Value is "true" or "1";
            node.WithOrderedPrefetch = physicalOpEl.Attribute("WithOrderedPrefetch")?.Value is "true" or "1";
            node.WithUnorderedPrefetch = physicalOpEl.Attribute("WithUnorderedPrefetch")?.Value is "true" or "1";

            // Hash Match properties
            node.ManyToMany = physicalOpEl.Attribute("ManyToMany")?.Value is "true" or "1";
            node.BitmapCreator = physicalOpEl.Attribute("BitmapCreator")?.Value is "true" or "1";

            // Parallelism properties
            node.Remoting = physicalOpEl.Attribute("Remoting")?.Value is "true" or "1";
            node.LocalParallelism = physicalOpEl.Attribute("LocalParallelism")?.Value is "true" or "1";

            // Wave 3.8: Spool Stack + PrimaryNodeId
            node.SpoolStack = physicalOpEl.Attribute("Stack")?.Value is "true" or "1";
            node.PrimaryNodeId = (int)ParseDouble(physicalOpEl.Attribute("PrimaryNodeId")?.Value);

            // Eager Index Spool — suggest CREATE INDEX from SeekPredicateNew + OutputList
            if (node.LogicalOp == "Eager Spool")
            {
                var spoolSeek = physicalOpEl.Element(Ns + "SeekPredicateNew")
                             ?? physicalOpEl.Element(Ns + "SeekPredicate");
                if (spoolSeek != null)
                {
                    var rangeCols = spoolSeek.Descendants(Ns + "RangeColumns")
                        .SelectMany(rc => rc.Elements(Ns + "ColumnReference"));

                    var keyColumns = new List<string>();
                    string? tblSchema = null;
                    string? tblName = null;

                    foreach (var col in rangeCols)
                    {
                        var colName = col.Attribute("Column")?.Value;
                        if (!string.IsNullOrEmpty(colName))
                            keyColumns.Add(colName);
                        tblSchema ??= col.Attribute("Schema")?.Value?.Replace("[", "").Replace("]", "");
                        tblName ??= col.Attribute("Table")?.Value?.Replace("[", "").Replace("]", "");
                    }

                    if (keyColumns.Count > 0 && !string.IsNullOrEmpty(tblName))
                    {
                        var includeCols = relOpEl.Element(Ns + "OutputList")?.Elements(Ns + "ColumnReference")
                            .Select(c => c.Attribute("Column")?.Value)
                            .Where(c => !string.IsNullOrEmpty(c) && !keyColumns.Contains(c))
                            .ToList() ?? new List<string?>();

                        var prefix = !string.IsNullOrEmpty(tblSchema) ? $"{tblSchema}.{tblName}" : tblName;
                        var keyStr = string.Join(", ", keyColumns);
                        var sql = $"CREATE INDEX [{string.Join("_", keyColumns)}] ON {prefix} ({keyStr})";
                        if (includeCols.Count > 0)
                            sql += $" INCLUDE ({string.Join(", ", includeCols)})";
                        sql += ";";
                        node.SuggestedIndex = sql;
                    }
                }
            }

            // Wave 3.9: Update DMLRequestSort + ActionColumn
            node.DMLRequestSort = physicalOpEl.Attribute("DMLRequestSort")?.Value is "true" or "1";
            var actionColEl = physicalOpEl.Element(Ns + "ActionColumn")?.Element(Ns + "ColumnReference");
            if (actionColEl != null)
                node.ActionColumn = FormatColumnRef(actionColEl);

            // SET predicate (UPDATE operator)
            var setPredicateEl = physicalOpEl.Element(Ns + "SetPredicate");
            if (setPredicateEl != null)
            {
                var so = setPredicateEl.Descendants(Ns + "ScalarOperator").FirstOrDefault();
                node.SetPredicate = so?.Attribute("ScalarString")?.Value;
            }

            // ActualJoinType from runtime info on adaptive joins
            node.ActualJoinType = physicalOpEl.Attribute("ActualJoinType")?.Value;

            // XSD gap: ForceSeekColumnCount (IndexScan)
            node.ForceSeekColumnCount = (int)ParseDouble(physicalOpEl.Attribute("ForceSeekColumnCount")?.Value);

            // XSD gap: PartitionId (IndexScan, TableScan, Sort, NestedLoops, AdaptiveJoin)
            var partitionIdEl = physicalOpEl.Element(Ns + "PartitionId");
            if (partitionIdEl != null)
            {
                var pidCols = partitionIdEl.Elements(Ns + "ColumnReference")
                    .Select(c => FormatColumnRef(c))
                    .Where(s => !string.IsNullOrEmpty(s));
                var pidStr = string.Join(", ", pidCols);
                if (!string.IsNullOrEmpty(pidStr))
                    node.PartitionId = pidStr;
            }

            // XSD gap: StarJoinInfo (Hash, Merge, NL, AdaptiveJoin)
            var starJoinEl = physicalOpEl.Element(Ns + "StarJoinInfo");
            if (starJoinEl != null)
            {
                node.IsStarJoin = starJoinEl.Attribute("Root")?.Value is "true" or "1";
                node.StarJoinOperationType = starJoinEl.Attribute("OperationType")?.Value;
            }

            // XSD gap: ProbeColumn (NL, Parallelism, Update)
            var probeColEl = physicalOpEl.Element(Ns + "ProbeColumn")?.Element(Ns + "ColumnReference");
            if (probeColEl != null)
                node.ProbeColumn = FormatColumnRef(probeColEl);

            // XSD gap: InRow (Parallelism)
            node.InRow = physicalOpEl.Attribute("InRow")?.Value is "true" or "1";

            // XSD gap: ComputeSequence (ComputeScalar)
            node.ComputeSequence = physicalOpEl.Attribute("ComputeSequence")?.Value is "true" or "1";

            // XSD gap: RollupInfo (StreamAggregate)
            var rollupEl = physicalOpEl.Element(Ns + "RollupInfo");
            if (rollupEl != null)
            {
                node.RollupHighestLevel = (int)ParseDouble(rollupEl.Attribute("HighestLevel")?.Value);
                foreach (var rlEl in rollupEl.Elements(Ns + "RollupLevel"))
                    node.RollupLevels.Add((int)ParseDouble(rlEl.Attribute("Level")?.Value));
            }

            // XSD gap: TVF ParameterList
            var tvfParamListEl = physicalOpEl.Element(Ns + "ParameterList");
            if (tvfParamListEl != null)
            {
                var tvfCols = tvfParamListEl.Elements(Ns + "ColumnReference")
                    .Select(c => FormatColumnRef(c))
                    .Where(s => !string.IsNullOrEmpty(s));
                var tvfStr = string.Join(", ", tvfCols);
                if (!string.IsNullOrEmpty(tvfStr))
                    node.TvfParameters = tvfStr;
                // Also check for ScalarOperator children (TVF can have scalar params)
                if (string.IsNullOrEmpty(node.TvfParameters))
                {
                    var tvfScalars = tvfParamListEl.Elements(Ns + "ScalarOperator")
                        .Select(s => s.Attribute("ScalarString")?.Value)
                        .Where(s => !string.IsNullOrEmpty(s));
                    var tvfScalarStr = string.Join(", ", tvfScalars);
                    if (!string.IsNullOrEmpty(tvfScalarStr))
                        node.TvfParameters = tvfScalarStr;
                }
            }

            // XSD gap: OriginalActionColumn (Update)
            var origActionColEl = physicalOpEl.Element(Ns + "OriginalActionColumn")?.Element(Ns + "ColumnReference");
            if (origActionColEl != null)
                node.OriginalActionColumn = FormatColumnRef(origActionColEl);

            // XSD gap: Scalar UDF structured detection
            foreach (var udfEl in ScopedDescendants(physicalOpEl, Ns + "UserDefinedFunction"))
            {
                var udfRef = new ScalarUdfReference
                {
                    FunctionName = udfEl.Attribute("FunctionName")?.Value?.Replace("[", "").Replace("]", "") ?? "",
                    IsClrFunction = udfEl.Attribute("IsClrFunction")?.Value is "true" or "1"
                };
                var clrEl = udfEl.Element(Ns + "CLRFunction");
                if (clrEl != null)
                {
                    udfRef.ClrAssembly = clrEl.Attribute("Assembly")?.Value;
                    udfRef.ClrClass = clrEl.Attribute("Class")?.Value;
                    udfRef.ClrMethod = clrEl.Attribute("Method")?.Value;
                }
                if (!string.IsNullOrEmpty(udfRef.FunctionName))
                    node.ScalarUdfs.Add(udfRef);
            }

            // XSD gap: TieColumns (Top operator)
            node.TieColumns = ParseColumnList(physicalOpEl, "TieColumns");

            // XSD gap: UDXName (Extension operator)
            node.UdxName = physicalOpEl.Attribute("UDXName")?.Value;

            // XSD gap: Operator-level IndexedViewInfo
            var opIvInfoEl = physicalOpEl.Element(Ns + "IndexedViewInfo");
            if (opIvInfoEl != null)
            {
                foreach (var ivObjEl in opIvInfoEl.Elements(Ns + "Object"))
                {
                    var ivDb = ivObjEl.Attribute("Database")?.Value?.Replace("[", "").Replace("]", "");
                    var ivSchema = ivObjEl.Attribute("Schema")?.Value?.Replace("[", "").Replace("]", "");
                    var ivTable = ivObjEl.Attribute("Table")?.Value?.Replace("[", "").Replace("]", "");
                    var ivIndex = ivObjEl.Attribute("Index")?.Value?.Replace("[", "").Replace("]", "");
                    var ivParts = new List<string>();
                    if (!string.IsNullOrEmpty(ivDb)) ivParts.Add(ivDb);
                    if (!string.IsNullOrEmpty(ivSchema)) ivParts.Add(ivSchema);
                    if (!string.IsNullOrEmpty(ivTable)) ivParts.Add(ivTable);
                    if (!string.IsNullOrEmpty(ivIndex)) ivParts.Add(ivIndex);
                    var ivName = string.Join(".", ivParts);
                    if (!string.IsNullOrEmpty(ivName))
                        node.OperatorIndexedViews.Add(ivName);
                }
            }

            // XSD gap: NamedParameterList (IndexScan)
            var namedParamListEl = physicalOpEl.Element(Ns + "NamedParameterList");
            if (namedParamListEl != null)
            {
                foreach (var npEl in namedParamListEl.Elements(Ns + "NamedParameter"))
                {
                    var np = new NamedParameterInfo
                    {
                        Name = npEl.Attribute("Name")?.Value ?? ""
                    };
                    var npScalar = npEl.Element(Ns + "ScalarOperator");
                    if (npScalar != null)
                        np.ScalarString = npScalar.Attribute("ScalarString")?.Value;
                    if (!string.IsNullOrEmpty(np.Name))
                        node.NamedParameters.Add(np);
                }
            }

            // XSD gap: Remote operator metadata
            node.RemoteDestination = physicalOpEl.Attribute("RemoteDestination")?.Value;
            node.RemoteSource = physicalOpEl.Attribute("RemoteSource")?.Value;
            node.RemoteObject = physicalOpEl.Attribute("RemoteObject")?.Value;
            node.RemoteQuery = physicalOpEl.Attribute("RemoteQuery")?.Value;

            // ForeignKeyReferenceCheck attributes
            node.ForeignKeyReferencesCount = (int)ParseDouble(physicalOpEl.Attribute("ForeignKeyReferencesCount")?.Value);
            node.NoMatchingIndexCount = (int)ParseDouble(physicalOpEl.Attribute("NoMatchingIndexCount")?.Value);
            node.PartialMatchingIndexCount = (int)ParseDouble(physicalOpEl.Attribute("PartialMatchingIndexCount")?.Value);

            // ConstantScan Values — parse Values/Row/ScalarOperator children
            var valuesEl = physicalOpEl.Element(Ns + "Values");
            if (valuesEl != null)
            {
                var rowParts = new List<string>();
                foreach (var rowEl in valuesEl.Elements(Ns + "Row"))
                {
                    var scalars = rowEl.Elements(Ns + "ScalarOperator")
                        .Select(s => s.Attribute("ScalarString")?.Value ?? "")
                        .Where(s => !string.IsNullOrEmpty(s));
                    var rowStr = string.Join(", ", scalars);
                    if (!string.IsNullOrEmpty(rowStr))
                        rowParts.Add($"({rowStr})");
                }
                if (rowParts.Count > 0)
                    node.ConstantScanValues = string.Join(", ", rowParts);
            }

            // UDX UsedUDXColumns — column references for CLR aggregate operators
            var udxColsEl = physicalOpEl.Element(Ns + "UsedUDXColumns");
            if (udxColsEl != null)
            {
                var udxCols = udxColsEl.Elements(Ns + "ColumnReference")
                    .Select(c => FormatColumnRef(c))
                    .Where(s => !string.IsNullOrEmpty(s));
                var udxColStr = string.Join(", ", udxCols);
                if (!string.IsNullOrEmpty(udxColStr))
                    node.UdxUsedColumns = udxColStr;
            }
        }

        // Output columns
        var outputList = relOpEl.Element(Ns + "OutputList");
        if (outputList != null)
        {
            var cols = outputList.Elements(Ns + "ColumnReference")
                .Select(c =>
                {
                    var col = c.Attribute("Column")?.Value ?? "";
                    var tbl = c.Attribute("Table")?.Value ?? "";
                    return string.IsNullOrEmpty(tbl) ? col : $"{tbl}.{col}";
                })
                .Where(s => !string.IsNullOrEmpty(s));
            var colList = string.Join(", ", cols);
            if (!string.IsNullOrEmpty(colList))
                node.OutputColumns = colList.Replace("[", "").Replace("]", "");
        }

        // Warnings
        node.Warnings = ParseWarnings(relOpEl);

        // SpillOccurred detail flag (node-level boolean)
        var warningsCheckEl = relOpEl.Element(Ns + "Warnings");
        if (warningsCheckEl?.Element(Ns + "SpillOccurred") != null)
            node.SpillOccurredDetail = true;

        // Wave 3.2: MemoryFractions (on RelOp)
        var memFracEl = relOpEl.Element(Ns + "MemoryFractions");
        if (memFracEl != null)
        {
            node.MemoryFractionInput = ParseDouble(memFracEl.Attribute("Input")?.Value);
            node.MemoryFractionOutput = ParseDouble(memFracEl.Attribute("Output")?.Value);
        }

        // Wave 3.3: RunTimePartitionSummary (on RelOp)
        var rtPartEl = relOpEl.Element(Ns + "RunTimePartitionSummary");
        if (rtPartEl != null)
        {
            var partAccEl = rtPartEl.Element(Ns + "PartitionsAccessed");
            if (partAccEl != null)
            {
                node.PartitionsAccessed = (int)ParseDouble(partAccEl.Attribute("PartitionCount")?.Value);
                var ranges = partAccEl.Elements(Ns + "PartitionRange")
                    .Select(r => $"{r.Attribute("Start")?.Value}-{r.Attribute("End")?.Value}");
                node.PartitionRanges = string.Join(", ", ranges);
                if (string.IsNullOrEmpty(node.PartitionRanges))
                    node.PartitionRanges = null;
            }
        }

        // Wave 2.4: Per-operator memory grants (MemoryGrant on RelOp)
        var memGrantEl = relOpEl.Element(Ns + "MemoryGrant");
        if (memGrantEl != null)
        {
            node.MemoryGrantKB = ParseLong(memGrantEl.Attribute("GrantedMemory")?.Value);
            node.DesiredMemoryKB = ParseLong(memGrantEl.Attribute("DesiredMemory")?.Value);
            node.MaxUsedMemoryKB = ParseLong(memGrantEl.Attribute("MaxUsedMemory")?.Value);
        }

        // Runtime information (actual plan)
        var runtimeEl = relOpEl.Element(Ns + "RunTimeInformation");
        if (runtimeEl != null)
        {
            node.HasActualStats = true;
            long totalRows = 0, totalExecutions = 0, totalRowsRead = 0;
            long totalRebinds = 0, totalRewinds = 0;
            long maxElapsed = 0, totalCpu = 0;
            long totalLogicalReads = 0, totalPhysicalReads = 0;
            long totalScans = 0, totalReadAheads = 0;
            long totalLobLogicalReads = 0, totalLobPhysicalReads = 0, totalLobReadAheads = 0;
            long totalSegmentReads = 0, totalSegmentSkips = 0;
            long totalUdfCpu = 0, maxUdfElapsed = 0;
            long maxInputMemoryGrant = 0, maxOutputMemoryGrant = 0, maxUsedMemoryGrant = 0;
            string? actualExecMode = null;

            foreach (var thread in runtimeEl.Elements(Ns + "RunTimeCountersPerThread"))
            {
                totalRows += ParseLong(thread.Attribute("ActualRows")?.Value);
                totalExecutions += ParseLong(thread.Attribute("ActualExecutions")?.Value);
                totalRowsRead += ParseLong(thread.Attribute("ActualRowsRead")?.Value);
                totalRebinds += ParseLong(thread.Attribute("ActualRebinds")?.Value);
                totalRewinds += ParseLong(thread.Attribute("ActualRewinds")?.Value);
                totalCpu += ParseLong(thread.Attribute("ActualCPUms")?.Value);
                totalLogicalReads += ParseLong(thread.Attribute("ActualLogicalReads")?.Value);
                totalPhysicalReads += ParseLong(thread.Attribute("ActualPhysicalReads")?.Value);
                totalScans += ParseLong(thread.Attribute("ActualScans")?.Value);
                totalReadAheads += ParseLong(thread.Attribute("ActualReadAheads")?.Value);
                totalLobLogicalReads += ParseLong(thread.Attribute("ActualLobLogicalReads")?.Value);
                totalLobPhysicalReads += ParseLong(thread.Attribute("ActualLobPhysicalReads")?.Value);
                totalLobReadAheads += ParseLong(thread.Attribute("ActualLobReadAheads")?.Value);

                // Wave 3.10: Columnstore segment reads/skips
                totalSegmentReads += ParseLong(thread.Attribute("ActualSegmentReads")?.Value);
                totalSegmentSkips += ParseLong(thread.Attribute("ActualSegmentSkips")?.Value);

                // Wave 3.11: UDF timing
                totalUdfCpu += ParseLong(thread.Attribute("UdfCpuTime")?.Value);
                var udfElapsed = ParseLong(thread.Attribute("UdfElapsedTime")?.Value);
                if (udfElapsed > maxUdfElapsed) maxUdfElapsed = udfElapsed;

                // Per-operator memory grant (same value on all threads, take max)
                var inputMem = ParseLong(thread.Attribute("InputMemoryGrant")?.Value);
                var outputMem = ParseLong(thread.Attribute("OutputMemoryGrant")?.Value);
                var usedMem = ParseLong(thread.Attribute("UsedMemoryGrant")?.Value);
                if (inputMem > maxInputMemoryGrant) maxInputMemoryGrant = inputMem;
                if (outputMem > maxOutputMemoryGrant) maxOutputMemoryGrant = outputMem;
                if (usedMem > maxUsedMemoryGrant) maxUsedMemoryGrant = usedMem;

                actualExecMode ??= thread.Attribute("ActualExecutionMode")?.Value;

                var elapsed = ParseLong(thread.Attribute("ActualElapsedms")?.Value);
                if (elapsed > maxElapsed) maxElapsed = elapsed;
            }

            node.ActualRows = totalRows;
            node.ActualExecutions = totalExecutions;
            node.ActualRowsRead = totalRowsRead;
            node.ActualRebinds = totalRebinds;
            node.ActualRewinds = totalRewinds;
            node.ActualElapsedMs = maxElapsed;
            node.ActualCPUMs = totalCpu;
            node.ActualLogicalReads = totalLogicalReads;
            node.ActualPhysicalReads = totalPhysicalReads;
            node.ActualScans = totalScans;
            node.ActualReadAheads = totalReadAheads;
            node.ActualLobLogicalReads = totalLobLogicalReads;
            node.ActualLobPhysicalReads = totalLobPhysicalReads;
            node.ActualLobReadAheads = totalLobReadAheads;
            node.ActualExecutionMode = actualExecMode;
            node.ActualSegmentReads = totalSegmentReads;
            node.ActualSegmentSkips = totalSegmentSkips;
            node.UdfCpuTimeMs = totalUdfCpu;
            node.UdfElapsedTimeMs = maxUdfElapsed;
            node.InputMemoryGrantKB = maxInputMemoryGrant;
            node.OutputMemoryGrantKB = maxOutputMemoryGrant;
            node.UsedMemoryGrantKB = maxUsedMemoryGrant;

            // Store per-thread data for parallel skew analysis
            foreach (var thread in runtimeEl.Elements(Ns + "RunTimeCountersPerThread"))
            {
                node.PerThreadStats.Add(new PerThreadRuntimeInfo
                {
                    ThreadId = (int)ParseDouble(thread.Attribute("Thread")?.Value),
                    ActualRows = ParseLong(thread.Attribute("ActualRows")?.Value),
                    ActualExecutions = ParseLong(thread.Attribute("ActualExecutions")?.Value),
                    ActualElapsedMs = ParseLong(thread.Attribute("ActualElapsedms")?.Value),
                    ActualCPUMs = ParseLong(thread.Attribute("ActualCPUms")?.Value),
                    ActualRowsRead = ParseLong(thread.Attribute("ActualRowsRead")?.Value),
                    ActualLogicalReads = ParseLong(thread.Attribute("ActualLogicalReads")?.Value),
                    ActualPhysicalReads = ParseLong(thread.Attribute("ActualPhysicalReads")?.Value),
                    ActualScans = ParseLong(thread.Attribute("ActualScans")?.Value),
                    ActualReadAheads = ParseLong(thread.Attribute("ActualReadAheads")?.Value),
                    FirstActiveTime = ParseLong(thread.Attribute("FirstActiveTime")?.Value),
                    LastActiveTime = ParseLong(thread.Attribute("LastActiveTime")?.Value),
                    OpenTime = ParseLong(thread.Attribute("OpenTime")?.Value),
                    FirstRowTime = ParseLong(thread.Attribute("FirstRowTime")?.Value),
                    LastRowTime = ParseLong(thread.Attribute("LastRowTime")?.Value),
                    CloseTime = ParseLong(thread.Attribute("CloseTime")?.Value),
                    InputMemoryGrant = ParseLong(thread.Attribute("InputMemoryGrant")?.Value),
                    OutputMemoryGrant = ParseLong(thread.Attribute("OutputMemoryGrant")?.Value),
                    UsedMemoryGrant = ParseLong(thread.Attribute("UsedMemoryGrant")?.Value),
                    Batches = ParseLong(thread.Attribute("Batches")?.Value),
                    ActualEndOfScans = ParseLong(thread.Attribute("ActualEndOfScans")?.Value),
                    ActualLocallyAggregatedRows = ParseLong(thread.Attribute("ActualLocallyAggregatedRows")?.Value),
                    IsInterleavedExecuted = thread.Attribute("IsInterleavedExecuted")?.Value is "true" or "1",
                    RowRequalifications = ParseLong(thread.Attribute("RowRequalifications")?.Value)
                });
            }
        }

        // Map to icon — done here so columnstore scans (Clustered/Index Scan
        // with Storage="ColumnStore") and Parallelism subtypes (which depend on
        // LogicalOp) can be routed to their specific icons.
        node.IconName = PlanIconMapper.GetIconName(node.PhysicalOp, node.StorageType, node.LogicalOp);

        // Recurse into child RelOps
        foreach (var childRelOp in FindChildRelOps(relOpEl))
        {
            var childNode = ParseRelOp(childRelOp, depth + 1);
            childNode.Parent = node;
            node.Children.Add(childNode);
        }

        return node;
    }

    private static XElement? GetOperatorElement(XElement relOpEl)
    {
        foreach (var child in relOpEl.Elements())
        {
            var name = child.Name.LocalName;
            if (name != "OutputList" && name != "RunTimeInformation" && name != "Warnings"
                && name != "MemoryFractions" && name != "RunTimePartitionSummary"
                && name != "MemoryGrant" && name != "InternalInfo")
            {
                return child;
            }
        }
        return null;
    }

    private static IEnumerable<XElement> FindChildRelOps(XElement relOpEl)
    {
        var operatorEl = GetOperatorElement(relOpEl);
        if (operatorEl == null) yield break;

        foreach (var child in operatorEl.Elements(Ns + "RelOp"))
            yield return child;

        foreach (var child in operatorEl.Elements())
        {
            if (child.Name.LocalName == "RelOp") continue;
            foreach (var nestedRelOp in child.Elements(Ns + "RelOp"))
                yield return nestedRelOp;
        }
    }
}
