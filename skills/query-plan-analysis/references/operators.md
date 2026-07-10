# Operators worth understanding

Not a catalogue. These are the ones whose presence changes what you should
conclude.

## Reading order

Plans display right to left, and data flows that way: leaves produce rows, the
root consumes them. But *logical* execution order is a depth-first walk from the
root — the root asks its first child for a row, which asks its first child, and so
on. Nested loops joins make this matter: the outer input (first child) drives, and
the inner input (second child) runs once per outer row.

In the XML, child `RelOp` elements appear nested inside the operator-specific
element (`<NestedLoops>`, `<Hash>`, `<Sort>`). **The first child `RelOp` is the
outer input.** Confusing outer and inner inputs inverts your entire analysis of a
nested loop.

## Joins

**Nested Loops** — for each row from the outer input, probe the inner. Optimal
when the outer is small and the inner has a supporting index. Catastrophic when
the outer input is large, because the inner runs once per row. Check
`ActualExecutions` on the inner side. The presence of `<OuterReferences>` means
the join correlates by passing values in, which is why a `NoJoinPredicate`
warning on a nested loop is often noise.

**Hash Match** — builds a hash table from the build input (first child), probes
with the second. Needs a memory grant. Blocking on the build side: no rows come
out until the build input is fully consumed. Spills when the grant is too small,
which is nearly always an underestimate on the build input. Fine for large,
unsorted, unindexed joins. Its presence is not a defect.

**Merge Join** — both inputs must be sorted on the join key. Very cheap when they
already are, because indexes provide the order. Expensive when the optimizer has
to add a `Sort` to make it work. A merge join with a sort beneath it is often a
worse choice than the hash join the optimizer rejected. A `many-to-many` merge
join uses a worktable in tempdb and is materially slower.

**Adaptive Join** (2017+, batch mode) — defers the hash-versus-loop decision until
runtime based on actual row count. `ActualJoinType` tells you what it picked.

## Spools

Spools materialize rows into a hidden temporary object in tempdb.

**Eager Spool** — reads its entire input before returning a single row. See
`indexes.md`. An eager *index* spool is the optimizer asking for an index.

**Lazy Spool** — reads on demand, returning rows as it goes. Cheaper.

**Table Spool / Row Count Spool** — caches a result for reuse.

An eager spool also appears legitimately in update plans, protecting against the
Halloween Problem. Do not report those as defects.

## Filter

A `Filter` operator applies a predicate that could not be pushed down into a scan
or seek. Its position matters: everything below it did work on rows the filter
then discarded. Compare its `ActualRows` to its child's — the difference is
wasted.

`Filter` with a `StartupExpression` is a startup filter guarding a branch that may
not execute at all. That is an optimization, not a problem.

## Compute Scalar

Usually free, and usually carries **no runtime statistics at all**. Do not
conclude that a Compute Scalar took zero time from its absent numbers; the work is
generally deferred and attributed to the operator that consumes its output. This
absence breaks naive self-time arithmetic (see `timing.md`).

A Compute Scalar invoking a scalar UDF is a very different animal. See below.

## Scalar UDFs

Before SQL Server 2019, a scalar UDF in a query:

- Runs once per row, as an interpreted call
- Is costed by the optimizer at essentially zero
- Forces the **entire plan serial** (`NonParallelPlanReason` =
  `TSQLUserDefinedFunctionsNotParallelizable`)
- Does not appear as its own operator, so its time hides inside whatever calls it

This combination — invisible in the plan, free according to cost, catastrophic in
reality — makes it the single most common cause of a query whose plan looks fine
and runs for minutes. Check `<QueryTimeStats UdfCpuTime="..." />`. If it is
present and large, that is your answer regardless of what the operators say.

SQL Server 2019 introduced Froid, which inlines many scalar UDFs into the calling
query. When inlining happens, the UDF's work becomes visible as real operators and
the plan may go parallel. Inlining is disabled by various constructs
(`WHILE` loops, time-dependent functions, `@@ROWCOUNT`, and others), so a 2019+
plan can still contain a non-inlined UDF.

## Sort

Blocking — nothing comes out until everything has gone in. Needs a memory grant.
Spills to tempdb when the grant is short.

A sort you did not ask for is the optimizer meeting a requirement of something
else: a merge join, a stream aggregate, a `DISTINCT`, an order-preserving
exchange. Removing the requirement removes the sort. An index that provides the
order removes it entirely.

`Sort (Top N Sort)` with a small N is cheap and does not need a full sort.

## Aggregates

**Stream Aggregate** — requires sorted input, aggregates as it goes, no memory
grant. Cheap when an index provides the order.

**Hash Match (Aggregate)** — no ordering requirement, needs a grant, can spill.

**Hash Match (Partial Aggregate)** — a pre-aggregation below an exchange in a
parallel plan, reducing rows before they cross threads. Its presence is good.

## Key Lookup / RID Lookup

See `indexes.md`. Judge by `ActualExecutions`, never by presence.

## Table Valued Function

A multi-statement TVF appears as a single operator with a fixed row estimate (1
before 2014, 100 after) regardless of what it returns. An inline TVF does not
appear at all — it is expanded into the calling query, which is why inline TVFs
are almost always preferable.

## Constant Scan

Produces a fixed set of rows, often zero or one. Common under `Nested Loops` for
`OR` expansion, and above an `Index Seek` in a `MERGE`. Rarely interesting.

## Parallelism

See `parallelism.md`. Its timings are unreliable and it is almost never the actual
problem, even when it looks expensive.
