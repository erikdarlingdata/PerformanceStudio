#!/usr/bin/env python3
"""
Flatten a SQL Server .sqlplan / showplan XML file into a compact text digest.

Reads nothing but the standard library. Handles UTF-16 (the default encoding
SSMS writes) and UTF-8 plans transparently.

    python extract.py plan.sqlplan
    python extract.py plan.sqlplan --top 15

The digest is ordered to match the triage procedure in SKILL.md: what kind of
plan this is, what SQL Server already told you, where time actually went, where
the estimates went wrong, and only then indexes.
"""

import argparse
import re
import sys
import xml.etree.ElementTree as ET

NS = "{http://schemas.microsoft.com/sqlserver/2004/07/showplan}"

EXCHANGE_LOGICAL = {"Gather Streams", "Distribute Streams", "Repartition Streams"}


def tag(el):
    """Local name of an element, namespace stripped."""
    return el.tag.split("}")[-1] if "}" in el.tag else el.tag


def num(el, name, default=0.0):
    v = el.get(name)
    if v is None:
        return default
    try:
        return float(v)
    except ValueError:
        return default


class Node:
    """One RelOp, with per-thread runtime stats folded to node level."""

    def __init__(self, el, parent=None):
        self.el = el
        self.parent = parent
        self.node_id = el.get("NodeId", "?")
        self.physical = el.get("PhysicalOp", "")
        self.logical = el.get("LogicalOp", "")
        self.est_rows = num(el, "EstimateRows")
        self.est_exec = num(el, "EstimateExecutions", 1.0)
        self.est_rebinds = num(el, "EstimateRebinds")
        self.est_rewinds = num(el, "EstimateRewinds")
        self.subtree_cost = num(el, "EstimatedTotalSubtreeCost")
        self.table_cardinality = num(el, "TableCardinality")
        self.parallel = el.get("Parallel") in ("1", "true")
        self.est_mode = el.get("EstimatedExecutionMode", "")
        # Present only when a row goal is in effect (TOP, FAST N, EXISTS...).
        self.row_goal = el.get("EstimateRowsWithoutRowGoal") is not None

        self.threads = []
        self.actual_mode = ""
        self.has_actual = False
        self.actual_rows = 0.0
        self.actual_rows_read = 0.0
        self.actual_executions = 0.0
        self.elapsed_ms = 0.0
        self.cpu_ms = 0.0
        self.logical_reads = 0.0

        rti = el.find(NS + "RunTimeInformation")
        if rti is not None:
            for t in rti.findall(NS + "RunTimeCountersPerThread"):
                self.threads.append(
                    {
                        "thread": int(num(t, "Thread")),
                        "rows": num(t, "ActualRows"),
                        "rows_read": num(t, "ActualRowsRead"),
                        "executions": num(t, "ActualExecutions"),
                        "elapsed": num(t, "ActualElapsedms"),
                        "cpu": num(t, "ActualCPUms"),
                        "reads": num(t, "ActualLogicalReads"),
                    }
                )
                if not self.actual_mode:
                    self.actual_mode = t.get("ActualExecutionMode", "")
            if self.threads:
                self.has_actual = True
                # Rows, CPU, reads SUM across threads. Elapsed takes the MAX.
                self.actual_rows = sum(t["rows"] for t in self.threads)
                self.actual_rows_read = sum(t["rows_read"] for t in self.threads)
                self.actual_executions = sum(t["executions"] for t in self.threads)
                self.cpu_ms = sum(t["cpu"] for t in self.threads)
                self.logical_reads = sum(t["reads"] for t in self.threads)
                self.elapsed_ms = max(t["elapsed"] for t in self.threads)

        self.children = [Node(c, self) for c in child_relops(el)]
        self.warnings = parse_warnings(el)

    @property
    def mode(self):
        return self.actual_mode or self.est_mode

    @property
    def is_exchange(self):
        return self.physical == "Parallelism" or self.logical in EXCHANGE_LOGICAL

    @property
    def label(self):
        if self.logical and self.logical != self.physical:
            return f"{self.physical} ({self.logical})"
        return self.physical


def child_relops(el):
    """
    Direct child RelOps. RelOps nest inside operator-specific elements
    (<NestedLoops>, <Hash>, ...), so descend until we hit the next RelOp
    and stop there.
    """
    found = []

    def walk(e):
        for c in e:
            if tag(c) == "RelOp":
                found.append(c)
            else:
                walk(c)

    walk(el)
    return found


def local_elements(relop_el):
    """Descendants of a RelOp that belong to it, not crossing into child RelOps."""
    out = []

    def walk(e):
        for c in e:
            if tag(c) == "RelOp":
                continue
            out.append(c)
            walk(c)

    walk(relop_el)
    return out


def unbracket(s):
    return (s or "").replace("[", "").replace("]", "")


def colref(c):
    parts = [c.get("Table"), c.get("Column")]
    return unbracket(".".join(p for p in parts if p))


def node_objects(node):
    """Tables/indexes this operator touches."""
    out = []
    for e in local_elements(node.el):
        if tag(e) != "Object":
            continue
        name = unbracket(f"{e.get('Schema', '')}.{e.get('Table', '')}").strip(".")
        idx = unbracket(e.get("Index", ""))
        alias = unbracket(e.get("Alias", ""))
        label = name
        if idx:
            label += f".{idx}"
        if alias:
            label += f" AS {alias}"
        out.append(label)
    return out


def node_predicate(node):
    for e in local_elements(node.el):
        if tag(e) == "Predicate":
            so = e.find(NS + "ScalarOperator")
            if so is not None and so.get("ScalarString"):
                return so.get("ScalarString")
    return None


def node_seek_predicates(node):
    """Reconstruct 'column op expression' for each seek key."""
    out = []
    for e in local_elements(node.el):
        if tag(e) != "SeekPredicateNew":
            continue
        for keys in e.findall(NS + "SeekKeys"):
            for part in keys:
                rc = part.find(NS + "RangeColumns")
                rx = part.find(NS + "RangeExpressions")
                cols = [colref(c) for c in rc] if rc is not None else []
                exprs = [so.get("ScalarString", "") for so in rx] if rx is not None else []
                scan_type = part.get("ScanType", "=")
                if cols:
                    out.append(f"{tag(part)}: {', '.join(cols)} {scan_type} {', '.join(exprs)}".strip())
    return out


def node_outer_references(node):
    for e in local_elements(node.el):
        if tag(e) == "OuterReferences":
            return [colref(c) for c in e.findall(NS + "ColumnReference")]
    return []


def node_output_list(node):
    ol = node.el.find(NS + "OutputList")
    if ol is None:
        return []
    return [colref(c) for c in ol.findall(NS + "ColumnReference")]


def node_scan_order(node):
    for e in local_elements(node.el):
        if e.get("Ordered") is not None:
            ordered = e.get("Ordered") in ("1", "true")
            direction = e.get("ScanDirection", "")
            return f"Ordered={'yes' if ordered else 'no'}" + (f" {direction}" if direction else "")
    return None


def is_eager_index_spool(node):
    """
    Index Spool specifically, NOT Table Spool. An eager *table* spool is ordinary
    Halloween protection in an update plan and is not a defect; only the eager
    *index* spool means "the optimizer built an index because you lack one," and
    only it suppresses the missing-index request.
    """
    return node.physical == "Index Spool" and "Eager" in node.logical


def own_cost(node):
    """
    Estimated cost attributable to this operator alone. Subtree cost is cumulative,
    so ranking by it always crowns the root. Still an ESTIMATE, in every plan.
    """
    return max(0.0, node.subtree_cost - sum(c.subtree_cost for c in node.children))


def fmt_duration(ms):
    """Raw ms is hard to feel above about a minute."""
    if ms < 60_000:
        return f"{ms:,.0f} ms"
    secs = ms / 1000.0
    h, rem = divmod(int(secs), 3600)
    m, s = divmod(rem, 60)
    human = f"{h}h{m:02d}m{s:02d}s" if h else f"{m}m{s:02d}s"
    return f"{ms:,.0f} ms ({human})"


# ---------------------------------------------------------------------------
# Self-time attribution.
#
# Row mode reports elapsed/CPU cumulatively: a node's number includes everything
# below it. Batch mode reports each operator standalone. Exchange operators
# accumulate downstream wait time, so their raw numbers mean little.
# ---------------------------------------------------------------------------


def sum_batch_subtree(node):
    """Batch operators pipeline, so their elapsed times add rather than nest."""
    total = node.elapsed_ms
    for c in node.children:
        if c.physical == "Parallelism":
            continue  # zone boundary
        if c.mode == "Batch" and c.has_actual:
            total += sum_batch_subtree(c)
        else:
            total += effective_child_elapsed(c)
    return total


def effective_child_elapsed(child):
    """Elapsed time a child contributes to its parent's subtree total."""
    if child.physical == "Parallelism" and child.children:
        return max(effective_child_elapsed(gc) for gc in child.children)
    if child.mode == "Batch" and child.has_actual:
        return sum_batch_subtree(child)
    if child.elapsed_ms > 0:
        return child.elapsed_ms
    if not child.children:
        return 0.0
    # Pass-through operator with no runtime stats (Compute Scalar and friends):
    # look through it to the first descendants that have them.
    return sum(effective_child_elapsed(gc) for gc in child.children)


def per_thread_self_elapsed(node):
    """
    Parallel row mode: subtract within a thread, never across threads, then take
    the slowest thread. Cross-thread subtraction produces nonsense.
    """
    parent_by_thread = {t["thread"]: t["elapsed"] for t in node.threads}
    child_by_thread = {}
    for child in node.children:
        target = child
        if child.physical == "Parallelism" and child.children:
            target = max(child.children, key=lambda c: c.elapsed_ms)
        for t in target.threads:
            child_by_thread[t["thread"]] = child_by_thread.get(t["thread"], 0.0) + t["elapsed"]
    best = 0.0
    for thread_id, parent_ms in parent_by_thread.items():
        self_ms = max(0.0, parent_ms - child_by_thread.get(thread_id, 0.0))
        best = max(best, self_ms)
    return best


def own_elapsed_ms(node):
    if not node.has_actual or node.elapsed_ms <= 0:
        return 0.0
    if node.mode == "Batch":
        return node.elapsed_ms
    if node.is_exchange:
        # Thread 0 is the coordinator; its elapsed is wall clock for the whole
        # branch, not this operator's work.
        workers = [t["elapsed"] for t in node.threads if t["thread"] > 0]
        if workers:
            return max(0.0, max(workers) - sum(effective_child_elapsed(c) for c in node.children))
        return 0.0
    if len(node.threads) > 1:
        return per_thread_self_elapsed(node)
    return max(0.0, node.elapsed_ms - sum(effective_child_elapsed(c) for c in node.children))


def effective_child_cpu(child):
    if child.physical == "Parallelism" and child.children:
        return max(effective_child_cpu(gc) for gc in child.children)
    if child.cpu_ms > 0:
        return child.cpu_ms
    if not child.children:
        return 0.0
    return sum(effective_child_cpu(gc) for gc in child.children)


def own_cpu_ms(node):
    """
    Self CPU. Reported CPU is cumulative in row mode exactly like elapsed, so a
    node's raw ActualCPUms includes every operator beneath it. Printing self
    elapsed next to cumulative CPU produces gibberish.
    """
    if not node.has_actual or node.cpu_ms <= 0:
        return 0.0
    if node.mode == "Batch":
        return node.cpu_ms  # standalone, exactly like batch-mode elapsed
    if len(node.threads) > 1:
        parent_by_thread = {t["thread"]: t["cpu"] for t in node.threads}
        child_by_thread = {}
        for child in node.children:
            target = child
            if child.physical == "Parallelism" and child.children:
                target = max(child.children, key=lambda c: c.cpu_ms)
            for t in target.threads:
                child_by_thread[t["thread"]] = child_by_thread.get(t["thread"], 0.0) + t["cpu"]
        best = 0.0
        for thread_id, parent_cpu in parent_by_thread.items():
            best = max(best, max(0.0, parent_cpu - child_by_thread.get(thread_id, 0.0)))
        return best
    return max(0.0, node.cpu_ms - sum(effective_child_cpu(c) for c in node.children))


# ---------------------------------------------------------------------------
# Warnings
# ---------------------------------------------------------------------------

WARN_FLAGS = [
    ("NoJoinPredicate", "No join predicate"),
    ("SpatialGuess", "Spatial index selectivity guessed"),
    ("UnmatchedIndexes", "Unmatched indexes (parameterization)"),
    ("FullUpdateForOnlineIndexBuild", "Full update for online index build"),
]


def parse_warnings(parent_el):
    w = parent_el.find(NS + "Warnings")
    if w is None:
        return []
    out = []

    for attr, text in WARN_FLAGS:
        if w.get(attr) in ("1", "true"):
            out.append(text)

    for c in w.findall(NS + "PlanAffectingConvert"):
        out.append(
            f"Implicit conversion [{c.get('ConvertIssue', '?')}]: {c.get('Expression', '')}"
        )

    spill = w.find(NS + "SpillToTempDb")
    level = spill.get("SpillLevel", "?") if spill is not None else None
    threads = spill.get("SpilledThreadCount", "?") if spill is not None else None

    for kind, el_name in (("Sort", "SortSpillDetails"), ("Hash", "HashSpillDetails")):
        for s in w.findall(NS + el_name):
            prefix = f"{kind} spill"
            if level is not None:
                prefix += f" level {level}, {threads} thread(s)"
            out.append(
                f"{prefix} - granted {num(s, 'GrantedMemoryKb'):,.0f} KB, "
                f"used {num(s, 'UsedMemoryKb'):,.0f} KB, "
                f"{num(s, 'WritesToTempDb'):,.0f} writes, "
                f"{num(s, 'ReadsFromTempDb'):,.0f} reads"
            )

    if spill is not None and not w.findall(NS + "SortSpillDetails") and not w.findall(NS + "HashSpillDetails"):
        out.append(f"Spill to tempdb, level {level}, {threads} thread(s)")

    for s in w.findall(NS + "ExchangeSpillDetails"):
        out.append(f"Exchange spill - {num(s, 'WritesToTempDb'):,.0f} writes to tempdb")

    if w.find(NS + "SpillOccurred") is not None:
        out.append("Spill occurred during execution")

    m = w.find(NS + "MemoryGrantWarning")
    if m is not None:
        out.append(
            f"Memory grant [{m.get('GrantWarningKind', '?')}]: "
            f"requested {num(m, 'RequestedMemory') / 1024:,.0f} MB, "
            f"granted {num(m, 'GrantedMemory') / 1024:,.0f} MB, "
            f"used {num(m, 'MaxUsedMemory') / 1024:,.0f} MB"
        )

    for el_name, text in (
        ("ColumnsWithNoStatistics", "No statistics on"),
        ("ColumnsWithStaleStatistics", "Stale statistics on"),
    ):
        e = w.find(NS + el_name)
        if e is not None:
            cols = [c.get("Column", "") for c in e.findall(NS + "ColumnReference")]
            out.append(f"{text}: {', '.join(filter(None, cols))}")

    for wait in w.findall(NS + "Wait"):
        out.append(f"Wait {wait.get('WaitType')}: {wait.get('WaitTime')}ms")

    return out


# ---------------------------------------------------------------------------
# Cardinality estimator default-guess fingerprints
# ---------------------------------------------------------------------------

CE_GUESSES = [
    (0.29, 0.31, "30% equality guess"),
    (0.098, 0.102, "10% inequality guess"),
    (0.088, 0.092, "9% LIKE/BETWEEN guess"),
    (0.155, 0.175, "~16.4% compound predicate guess"),
    (0.009, 0.011, "1% multi-inequality guess"),
]


def detect_ce_guess(est_rows, table_cardinality):
    if table_cardinality <= 0:
        return None
    sel = est_rows / table_cardinality
    for lo, hi, name in CE_GUESSES:
        if lo <= sel <= hi:
            return f"{name} ({sel * 100:.1f}% of {table_cardinality:,.0f})"
    return None


# ---------------------------------------------------------------------------
# Digest
# ---------------------------------------------------------------------------


def flatten(node, acc=None):
    acc = acc if acc is not None else []
    acc.append(node)
    for c in node.children:
        flatten(c, acc)
    return acc


def fmt_rows(n):
    return f"{n:,.0f}" if n >= 1 else f"{n:.4g}"


def render_tree(node, out, has_actual, depth=0, budget=None):
    """
    Indented operator tree. This is the only view of plan SHAPE, and on an
    estimated plan it is the only thing there is to reason about.
    """
    if budget is not None:
        if budget[0] <= 0:
            return
        budget[0] -= 1
    indent = "  " + "   " * depth
    objs = node_objects(node)
    obj = f"  {objs[0]}" if objs else ""
    if has_actual and node.has_actual:
        execs = max(1.0, node.actual_executions)
        detail = (
            f"est {fmt_rows(node.est_rows)}/exec vs "
            f"actual {fmt_rows(node.actual_rows / execs)}/exec"
        )
    else:
        detail = f"est {fmt_rows(node.est_rows)} rows, cost {own_cost(node):,.2f}"
    out.append(f"{indent}[{node.node_id}] {node.label}  ({detail}){obj}")
    for c in node.children:
        render_tree(c, out, has_actual, depth + 1, budget)


def statement_text(stmt_el):
    return " ".join((stmt_el.get("StatementText") or "").strip().split())


def describe_statement(stmt_el, out, top_n, full_sql=False):
    text = statement_text(stmt_el)
    stmt_id = stmt_el.get("StatementId", "?")
    if not full_sql and len(text) > 1500:
        text = text[:1500] + f" ... [truncated; see --sql {stmt_id} for the full text]"

    out.append("=" * 78)
    out.append(f"STATEMENT {stmt_id}  [{stmt_el.get('StatementType', '?')}]")
    out.append("=" * 78)
    out.append(f"  {text}")
    out.append("")

    qp = stmt_el.find(NS + "QueryPlan")
    if qp is None:
        out.append("  (no query plan on this statement)")
        out.append("")
        return

    # --- 1. What kind of plan is this? -------------------------------------
    root_el = qp.find(NS + "RelOp")
    if root_el is None:
        out.append("  (no operators)")
        out.append("")
        return
    root = Node(root_el)
    nodes = flatten(root)
    has_actual = any(n.has_actual for n in nodes)

    qts = qp.find(NS + "QueryTimeStats")
    out.append("-- PLAN TYPE ------------------------------------------------")
    out.append(f"  Runtime stats present : {'YES (actual plan)' if has_actual else 'NO (ESTIMATED plan)'}")
    out.append(f"  CE model version      : {stmt_el.get('CardinalityEstimationModelVersion', '?')}")
    out.append(f"  Optimization level    : {stmt_el.get('StatementOptmLevel', '?')}")
    early_abort = stmt_el.get("StatementOptmEarlyAbortReason")
    if early_abort:
        out.append(f"  Early abort reason    : {early_abort}")
    out.append(f"  Estimated subtree cost: {num(stmt_el, 'StatementSubTreeCost'):,.2f}  (ALWAYS an estimate)")
    dop = qp.get("DegreeOfParallelism")
    if dop:
        out.append(f"  Degree of parallelism : {dop}")
    npr = qp.get("NonParallelPlanReason")
    if npr:
        out.append(f"  Non-parallel reason   : {npr}")
    if qts is not None:
        total_elapsed = num(qts, "ElapsedTime")
        out.append(
            f"  Query time            : {fmt_duration(total_elapsed)} elapsed, "
            f"{fmt_duration(num(qts, 'CpuTime'))} CPU"
        )
        udf_cpu = num(qts, "UdfCpuTime")
        udf_elapsed = num(qts, "UdfElapsedTime")
        if udf_cpu > 0 or udf_elapsed > 0:
            out.append(
                f"  UDF time              : {fmt_duration(udf_elapsed)} elapsed, "
                f"{fmt_duration(udf_cpu)} CPU"
            )
            if total_elapsed > 0:
                pct = udf_elapsed / total_elapsed * 100
                out.append(
                    f"    -> scalar UDFs account for {pct:.2f}% of elapsed time. "
                    f"{'This is the query.' if pct > 50 else ''}".rstrip()
                )
            # Per-invocation cost is the most persuasive number available, and the
            # UDF runs once per row of whichever operator computes it.
            udf_rows = max(
                (n.actual_rows for n in nodes if n.physical == "Compute Scalar" and n.has_actual),
                default=0,
            )
            if udf_rows > 0 and udf_elapsed > 0:
                out.append(
                    f"    -> ~{udf_elapsed / udf_rows:,.1f} ms elapsed per invocation "
                    f"across {udf_rows:,.0f} rows"
                )
    out.append("")

    # --- 2. What did SQL Server already tell you? --------------------------
    out.append("-- WARNINGS -------------------------------------------------")
    plan_warnings = parse_warnings(qp)
    any_warning = bool(plan_warnings)
    for w in plan_warnings:
        out.append(f"  [plan] {w}")
    for n in nodes:
        for w in n.warnings:
            any_warning = True
            out.append(f"  [node {n.node_id} {n.label}] {w}")
    if not any_warning:
        out.append("  (none)")
    out.append("")

    # --- Memory grant ------------------------------------------------------
    mg = qp.find(NS + "MemoryGrantInfo")
    if mg is not None and (mg.get("GrantedMemory") or mg.get("RequestedMemory")):
        out.append("-- MEMORY GRANT (KB) ----------------------------------------")
        for a in (
            "SerialRequiredMemory",
            "SerialDesiredMemory",
            "RequestedMemory",
            "GrantedMemory",
            "MaxUsedMemory",
            "MaxQueryMemory",
            "GrantWaitTime",
        ):
            if mg.get(a) is not None:
                out.append(f"  {a:22}: {num(mg, a):,.0f}")
        granted, used = num(mg, "GrantedMemory"), num(mg, "MaxUsedMemory")
        if granted > 0 and used > 0:
            out.append(f"  -> used {used / granted * 100:.1f}% of the grant")
        out.append("")

    # --- Parameters --------------------------------------------------------
    params = qp.find(NS + "ParameterList")
    if params is not None:
        rows = []
        for c in params.findall(NS + "ColumnReference"):
            compiled = c.get("ParameterCompiledValue")
            runtime = c.get("ParameterRuntimeValue")
            if compiled is None and runtime is None:
                continue
            flag = ""
            if compiled is None:
                flag = "   (no compiled value: not sniffed - local variable, or RECOMPILE)"
            elif runtime is not None and compiled != runtime:
                flag = "   <-- compiled for a different value than it ran with"
            rows.append(
                f"  {c.get('Column', '?')}: compiled={compiled or '(none)'} "
                f"runtime={runtime or '(none)'}{flag}"
            )
        if rows:
            out.append("-- PARAMETERS -----------------------------------------------")
            out.extend(rows)
            out.append("")

    # --- 3. Where did time actually go? ------------------------------------
    hot = []
    if has_actual:
        out.append(f"-- TOP {top_n} OPERATORS BY SELF ELAPSED TIME (not cost) -----")
        out.append("  'self' = this operator's own work, children subtracted out.")
        out.append("  Sorted by self elapsed. Self CPU is a SEPARATE clock: it sums across")
        out.append("  threads while elapsed takes the slowest thread, so CPU exceeding")
        out.append("  elapsed means parallelism, not a problem. Never quote one as the other.")
        out.append("")
        out.append(f"  {'self elapsed':>14}  {'self CPU':>12}  {'rows out':>14}   node  operator")
        timed = [(own_elapsed_ms(n), n) for n in nodes]
        timed = [(ms, n) for ms, n in timed if ms > 0]
        timed.sort(key=lambda x: -x[0])
        if not timed:
            out.append("  (no operator elapsed times recorded)")
        for ms, n in timed[:top_n]:
            hot.append(n)
            note = "   [exchange: raw times unreliable]" if n.is_exchange else ""
            out.append(
                f"  {ms:11,.0f} ms  {own_cpu_ms(n):9,.0f} ms  {fmt_rows(n.actual_rows):>14}   "
                f"{n.node_id:>4}  {n.label}{note}"
            )
            # Rows read vs rows emitted: the tell for a predicate applied as a
            # filter instead of a seek. Only meaningful when SQL Server recorded it.
            if n.actual_rows_read > 0 and n.actual_rows > 0:
                ratio = n.actual_rows_read / n.actual_rows
                if ratio >= 2:
                    if n.row_goal:
                        flag = "   [row goal: stopped early, did not read the table]"
                    elif ratio >= 100:
                        flag = "   <-- reads far more than it returns"
                    else:
                        flag = ""
                    out.append(
                        f"  {'':>14}  {'':>12}  read {fmt_rows(n.actual_rows_read)} rows "
                        f"to emit {fmt_rows(n.actual_rows)} ({ratio:,.0f}x){flag}"
                    )
        out.append("")

    else:
        # No runtime data, so shape and estimated cost are all there is. Rank by
        # SELF cost, since subtree cost is cumulative and always crowns the root.
        out.append(f"-- TOP {top_n} OPERATORS BY ESTIMATED SELF COST ---------------")
        out.append("  ESTIMATES, not measurements. Nothing ran. Cost cannot tell you")
        out.append("  what was slow - it can only tell you what the optimizer feared.")
        out.append("")
        costed = sorted(((own_cost(n), n) for n in nodes), key=lambda x: -x[0])
        for c, n in costed[:top_n]:
            if c <= 0:
                continue
            objs = node_objects(n)
            out.append(
                f"  {c:12,.2f}  node {n.node_id:>3}  {n.label} "
                f"(est {fmt_rows(n.est_rows)} rows)"
                + (f"  {objs[0]}" if objs else "")
            )
        out.append("")

    # --- Plan shape --------------------------------------------------------
    out.append("-- OPERATOR TREE -------------------------------------------")
    out.append("  Children are indented. The FIRST child of a join is its outer input.")
    budget = [80]
    render_tree(root, out, has_actual, budget=budget)
    if budget[0] <= 0:
        out.append(f"  ... tree truncated at 80 operators of {len(nodes)}")
    out.append("")

    # Operators the digest names elsewhere; their predicates get printed below so
    # nobody has to open the raw XML to follow up on a node we pointed them at.
    cited = list(hot)

    # --- 4. Where are the estimates wrong? ---------------------------------
    if has_actual:
        out.append("-- CARDINALITY SKEW (per execution) -------------------------")
        skewed = []
        for n in nodes:
            if not n.has_actual or n.is_exchange:
                continue
            execs = max(1.0, n.actual_executions)
            actual_per_exec = n.actual_rows / execs
            est = n.est_rows  # already per-execution in showplan
            if est <= 0 and actual_per_exec <= 0:
                continue
            ratio = (actual_per_exec + 1) / (est + 1)
            if ratio >= 10 or ratio <= 0.1:
                skewed.append((abs(ratio if ratio >= 1 else 1 / ratio), n, est, actual_per_exec, execs, ratio))
        skewed.sort(key=lambda x: -x[0])
        if not skewed:
            out.append("  (no operator off by 10x or more)")
        for _, n, est, act, execs, ratio in skewed[:top_n]:
            cited.append(n)
            direction = "under" if ratio > 1 else "over"
            out.append(
                f"  node {n.node_id:>3} {n.label}: est {fmt_rows(est)}/exec vs actual "
                f"{fmt_rows(act)}/exec over {fmt_rows(execs)} exec(s) -> {direction}estimated "
                f"{(ratio if ratio >= 1 else 1 / ratio):,.1f}x"
            )
            guess = detect_ce_guess(n.est_rows, n.table_cardinality)
            if guess:
                out.append(f"           estimate {guess} - the optimizer had no useful statistics")
        out.append("")

        # --- 5. Thread skew ------------------------------------------------
        skewed_nodes = []
        for n in nodes:
            if len(n.threads) <= 1:
                continue
            workers = [t["rows"] for t in n.threads if t["thread"] > 0]
            if len(workers) < 2:
                continue
            hi, lo = max(workers), min(workers)
            # Ignore trivial row counts; a 4x imbalance over 12 rows means nothing.
            if hi < 100 or (lo > 0 and hi / lo < 4):
                continue
            idle = sum(1 for w in workers if w == 0)
            skewed_nodes.append((hi, n, hi, lo, len(workers), idle))
        if skewed_nodes:
            skewed_nodes.sort(key=lambda x: -x[0])
            out.append("-- PARALLEL THREAD SKEW ------------------------------------")
            all_idle = [s for s in skewed_nodes if s[5] == s[4] - 1]
            if len(all_idle) >= 3:
                out.append(
                    f"  {len(all_idle)} operators did ALL their work on a single thread "
                    f"(every other worker got 0 rows) - the parallel branch is effectively serial."
                )
            for _, n, hi, lo, workers, idle in skewed_nodes[:top_n]:
                cited.append(n)
                out.append(
                    f"  node {n.node_id:>3} {n.label}: busiest {hi:,.0f} rows, "
                    f"quietest {lo:,.0f} rows, {idle} of {workers} workers idle"
                )
            if len(skewed_nodes) > top_n:
                out.append(f"  ... and {len(skewed_nodes) - top_n} more skewed operators")
            out.append("")

    # --- Waits -------------------------------------------------------------
    ws = qp.find(NS + "WaitStats")
    if ws is not None:
        waits = ws.findall(NS + "Wait")
        if waits:
            out.append("-- TOP WAITS ------------------------------------------------")
            for w in sorted(waits, key=lambda x: -num(x, "WaitTimeMs"))[:10]:
                out.append(
                    f"  {w.get('WaitType', '?'):32} {num(w, 'WaitTimeMs'):>9,.0f} ms "
                    f"({num(w, 'WaitCount'):,.0f} waits)"
                )
            out.append("")

    # --- 6. Missing indexes (read as a hint, never as DDL) -----------------
    # --- Predicates for every operator the digest pointed at ---------------
    # indexes.md says to design an index from the spool's SeekPredicate, and
    # warnings.md says to diagnose a no-join-predicate from the join's INPUTS.
    # Surface all of it so nobody has to open the raw XML.
    interesting = list(cited)
    for n in nodes:
        if not (n.warnings or is_eager_index_spool(n)):
            continue
        if n not in interesting:
            interesting.append(n)
        # A warned join is diagnosed from its children: are both pinned to the
        # same constant? Without them the reader sees half the discriminator.
        for child in n.children:
            if child not in interesting:
                interesting.append(child)
    seen_ids = set()
    ordered = []
    for n in interesting:
        if id(n) not in seen_ids:
            seen_ids.add(id(n))
            ordered.append(n)
    ordered.sort(key=lambda n: nodes.index(n))

    detail_lines = []
    for n in ordered:
        bits = []
        objs = node_objects(n)
        if objs:
            bits.append(f"    object    : {', '.join(dict.fromkeys(objs))}")
        order = node_scan_order(n)
        if order:
            bits.append(f"    scan      : {order}")
        for sp in node_seek_predicates(n):
            bits.append(f"    seek      : {sp}")
        pred = node_predicate(n)
        if pred:
            bits.append(f"    predicate : {pred}")
        outer = node_outer_references(n)
        if outer:
            bits.append(
                f"    outer refs: {', '.join(outer)}  "
                f"(correlated - a join here needs no predicate)"
            )
        if n.row_goal:
            bits.append(
                "    row goal  : active (TOP/FAST/EXISTS) - a scan may stop early, so a "
                "large rows-read count does not mean it read the whole table"
            )
        # For a no-join-predicate warning: did the output actually multiply?
        if any("No join predicate" in w for w in n.warnings) and n.has_actual:
            inputs = [c.actual_rows for c in n.children if c.has_actual]
            if len(inputs) == 2:
                a, b = inputs
                product = a * b
                emitted = n.actual_rows
                bits.append(
                    f"    row check : inputs {fmt_rows(a)} and {fmt_rows(b)}; a cross join "
                    f"would emit {fmt_rows(product)}; this join emitted {fmt_rows(emitted)}"
                )
                # Only a product meaningfully larger than either input can discriminate.
                if product <= max(a, b) or min(a, b) <= 1:
                    bits.append(
                        "                INCONCLUSIVE - an input has <=1 row (often a row "
                        "goal), so multiplication cannot be observed. Judge from the "
                        "predicates above instead."
                    )
                elif emitted < product / 2:
                    bits.append(
                        "                Output did not multiply, so this is NOT an "
                        "accidental cross join."
                    )
                else:
                    bits.append(
                        "                Output is close to the product: consistent with a "
                        "GENUINE cross join. Check the predicates above."
                    )
        if bits:
            detail_lines.append(f"  node {n.node_id} {n.label}")
            detail_lines.extend(bits)
    if detail_lines:
        out.append("-- PREDICATES ON CITED OPERATORS ----------------------------")
        out.extend(detail_lines)
        out.append("")

    out.append("-- MISSING INDEX REQUESTS (hints, NOT ready-to-run DDL) -----")
    mi_root = qp.find(NS + "MissingIndexes")
    seen_requests = {}
    if mi_root is not None:
        for group in mi_root.findall(NS + "MissingIndexGroup"):
            impact = num(group, "Impact")
            for mi in group.findall(NS + "MissingIndex"):
                table = unbracket(f"{mi.get('Schema', '')}.{mi.get('Table', '')}")
                cols = []
                for cg in mi.findall(NS + "ColumnGroup"):
                    names = [c.get("Name", "") for c in cg.findall(NS + "Column")]
                    cols.append((cg.get("Usage", "?"), tuple(names)))
                key = (table, tuple(cols))
                # Near-identical requests differing only in impact are noise.
                seen_requests[key] = max(seen_requests.get(key, 0.0), impact)
    if not seen_requests:
        out.append("  (none)")
        spools = [n for n in nodes if is_eager_index_spool(n)]
        if spools:
            ids = ", ".join(n.node_id for n in spools)
            out.append(
                f"  NOTE: an eager INDEX spool is present (node {ids}). Index spools"
            )
            out.append(
                "  suppress the missing-index request, so 'none' here does NOT mean no"
            )
            out.append(
                "  index is needed. Build the index from the spool's seek predicate."
            )
    else:
        for (table, cols), impact in sorted(seen_requests.items(), key=lambda x: -x[1]):
            out.append(f"  {table}  (claimed impact {impact:.1f}%, of an ESTIMATED cost)")
            for usage, names in cols:
                out.append(f"    {usage:10}: {', '.join(names)}")
        out.append("  NOTE: equality column order is arbitrary; existing indexes are ignored.")
    out.append("")


def load_plan(path):
    """
    Parse a showplan file, ignoring the encoding declared in the XML prolog.

    SSMS writes .sqlplan as UTF-16, but a plan that has been opened and re-saved
    in an editor is often UTF-8 bytes still carrying encoding="utf-16". Trusting
    the declaration makes those files unparseable, so detect from the bytes and
    strip the prolog before handing a str to ElementTree.
    """
    with open(path, "rb") as f:
        raw = f.read()

    if raw.startswith(b"\xff\xfe"):
        text = raw.decode("utf-16-le", errors="replace")[1:]
    elif raw.startswith(b"\xfe\xff"):
        text = raw.decode("utf-16-be", errors="replace")[1:]
    elif raw.startswith(b"\xef\xbb\xbf"):
        text = raw.decode("utf-8-sig", errors="replace")
    elif len(raw) > 1 and raw[1] == 0:
        text = raw.decode("utf-16-le", errors="replace")  # UTF-16 LE, no BOM
    elif len(raw) > 1 and raw[0] == 0:
        text = raw.decode("utf-16-be", errors="replace")  # UTF-16 BE, no BOM
    else:
        text = raw.decode("utf-8", errors="replace")

    text = re.sub(r"^\s*<\?xml.*?\?>", "", text, count=1, flags=re.DOTALL)
    return ET.fromstring(text.strip())


def describe_node(node, out):
    """Everything known about one operator. The sanctioned alternative to
    opening the raw XML."""
    out.append("=" * 78)
    out.append(f"NODE {node.node_id}: {node.label}")
    out.append("=" * 78)
    out.append(f"  execution mode   : {node.mode or '(unspecified)'}")
    if node.parallel:
        out.append("  parallel         : yes")
    for label, value in (
        ("object", ", ".join(dict.fromkeys(node_objects(node)))),
        ("scan order", node_scan_order(node)),
        ("predicate", node_predicate(node)),
        ("outer references", ", ".join(node_outer_references(node))),
    ):
        if value:
            out.append(f"  {label:17}: {value}")
    for sp in node_seek_predicates(node):
        out.append(f"  seek predicate   : {sp}")
    outputs = node_output_list(node)
    if outputs:
        out.append(f"  output columns   : {', '.join(outputs)}")
    out.append("")
    out.append("  ESTIMATES")
    out.append(f"    rows per execution : {fmt_rows(node.est_rows)}")
    if node.table_cardinality:
        out.append(f"    table cardinality  : {fmt_rows(node.table_cardinality)}")
        guess = detect_ce_guess(node.est_rows, node.table_cardinality)
        if guess:
            out.append(f"    !! estimate {guess}")
    out.append(f"    subtree cost       : {node.subtree_cost:,.4f}  (an estimate, always)")

    if not node.has_actual:
        out.append("")
        out.append("  No runtime statistics on this operator.")
        return
    out.append("")
    out.append("  ACTUALS")
    out.append(f"    executions         : {fmt_rows(node.actual_executions)}")
    out.append(f"    rows emitted       : {fmt_rows(node.actual_rows)} (total, all executions)")
    if node.actual_executions > 0:
        out.append(f"    rows per execution : {fmt_rows(node.actual_rows / node.actual_executions)}")
    if node.actual_rows_read:
        out.append(f"    rows READ          : {fmt_rows(node.actual_rows_read)}")
    if node.logical_reads:
        out.append(f"    logical reads      : {fmt_rows(node.logical_reads)}")
    out.append(f"    self elapsed       : {fmt_duration(own_elapsed_ms(node))}")
    out.append(f"    self CPU           : {fmt_duration(own_cpu_ms(node))}")
    out.append(f"    cumulative elapsed : {fmt_duration(node.elapsed_ms)}  (includes children in row mode)")
    if len(node.threads) > 1:
        out.append("")
        out.append("  PER THREAD (thread 0 is the coordinator, not a worker)")
        for t in sorted(node.threads, key=lambda x: x["thread"]):
            out.append(
                f"    thread {t['thread']:>2}: {t['rows']:>14,.0f} rows  "
                f"{t['elapsed']:>10,.0f} ms elapsed  {t['cpu']:>10,.0f} ms CPU"
            )
    if node.warnings:
        out.append("")
        out.append("  WARNINGS")
        for w in node.warnings:
            out.append(f"    {w}")


def main():
    ap = argparse.ArgumentParser(description="Flatten a .sqlplan into a text digest.")
    ap.add_argument("plan", help="path to a .sqlplan / showplan XML file")
    ap.add_argument("--top", type=int, default=10, help="rows to show in ranked sections")
    ap.add_argument(
        "--node",
        metavar="ID",
        help="print full detail for one operator (predicates, per-thread stats) "
        "instead of the digest. NodeIds repeat across statements; every match is "
        "printed, each tagged with its statement.",
    )
    ap.add_argument(
        "--sql",
        nargs="?",
        const="*",
        metavar="STMT",
        help="print the full, untruncated statement text (optionally for one "
        "StatementId) and exit",
    )
    args = ap.parse_args()

    try:
        root = load_plan(args.plan)
    except OSError as e:
        print(f"error: could not read {args.plan}: {e}", file=sys.stderr)
        return 1
    except ET.ParseError as e:
        print(
            f"error: {args.plan} is not a valid showplan XML document: {e}\n"
            f"       (a .sqlplan must have a <ShowPlanXML> root element)",
            file=sys.stderr,
        )
        return 1

    if tag(root) != "ShowPlanXML":
        print(
            f"error: {args.plan} parsed as <{tag(root)}>, not <ShowPlanXML>. "
            f"This is not a query plan.",
            file=sys.stderr,
        )
        return 1

    out = []
    header = f"PLAN DIGEST: {args.plan}"
    out.append(header)
    out.append(
        f"SQL Server build {root.get('Build', '?')}, showplan schema {root.get('Version', '?')}"
    )
    out.append("")

    # Only statements that carry a plan. SET NOCOUNT ON and friends have none.
    stmts = [el for el in root.iter() if tag(el) == "StmtSimple" and el.find(NS + "QueryPlan") is not None]
    if not stmts:
        print(f"error: {args.plan} contains no statements with a query plan", file=sys.stderr)
        return 1
    if args.sql is not None:
        lines = []
        for stmt in stmts:
            sid = stmt.get("StatementId", "?")
            if args.sql != "*" and sid != args.sql:
                continue
            lines.append(f"-- StatementId {sid} [{stmt.get('StatementType', '?')}]")
            lines.append(statement_text(stmt))
            lines.append("")
        if not lines:
            print(f"error: no statement with StatementId {args.sql}", file=sys.stderr)
            return 1
        print("\n".join(lines))
        return 0

    if args.node is not None:
        detail = []
        for stmt in stmts:
            root_el = stmt.find(NS + "QueryPlan").find(NS + "RelOp")
            if root_el is None:
                continue
            sid = stmt.get("StatementId", "?")
            for n in flatten(Node(root_el)):
                if n.node_id != args.node:
                    continue
                # NodeIds are unique per statement, not per plan. Say which one.
                detail.append(f"### StatementId {sid}: {statement_text(stmt)[:110]}")
                describe_node(n, detail)
                detail.append("")
        if not detail:
            print(f"error: no operator with NodeId {args.node} in {args.plan}", file=sys.stderr)
            return 1
        print("\n".join(detail))
        return 0

    if len(stmts) > 1:
        out.append(f"{len(stmts)} statements carry a plan. Each is analyzed separately below.")
        out.append("")

    for stmt in stmts:
        describe_statement(stmt, out, args.top)

    print("\n".join(out))
    return 0


if __name__ == "__main__":
    sys.exit(main())
