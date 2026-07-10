# Warnings SQL Server puts in the plan

These require no inference. They are the highest signal-to-noise content in a
plan. They appear in a `<Warnings>` element, which can hang off the `QueryPlan`
(statement-level) or off any `RelOp` (operator-level). The location matters —
"a spill happened" is much less useful than "the hash join at node 3 spilled."

Some are attributes on `<Warnings>`; some are child elements. Both are listed
below.

## Implicit conversion — `<PlanAffectingConvert>`

Attributes: `ConvertIssue`, `Expression`.

Two distinct issues share this element, and they are not equally serious:

- **`ConvertIssue="Seek Plan"`** — the conversion prevented an index seek. The
  query is scanning where it could have sought. This is usually the expensive
  one.
- **`ConvertIssue="Cardinality Estimate"`** — the conversion did not block the
  seek but did prevent the optimizer from using the histogram, so the estimate
  is a guess. Bad plan shape downstream.

Cause: comparing columns of different types, so SQL Server converts one side.
The conversion happens on whichever side has *lower datatype precedence*. If the
column loses, the index on it becomes unusable for seeking.

Most common in practice:

- `NVARCHAR` parameter compared to a `VARCHAR` column. `NVARCHAR` has higher
  precedence, so the *column* gets converted. Very common with ORMs, which
  default to sending Unicode. This one blocks seeks.
- `VARCHAR` parameter against an `NVARCHAR` column converts the *parameter*,
  which is harmless.
- Numeric types against string columns, always bad.
- A `DATE` column compared against a `DATETIME` literal.

Fix by making the types match — change the parameter's type, or the column's.
Do not fix it by wrapping the column in `CAST`, which makes the predicate
non-SARGable and achieves nothing.

Note that SQL Server does not always emit this warning even when an implicit
conversion is present. Its absence is not proof.

## Spills — `<SpillToTempDb>`, `<SortSpillDetails>`, `<HashSpillDetails>`, `<ExchangeSpillDetails>`

The operator asked for memory, got less than it needed, and wrote to tempdb.

`SpillLevel` matters. Level 1 is a single pass. Higher levels mean recursive
spilling — the spilled partition itself did not fit and spilled again — and cost
grows sharply. Level 3 and above is pathological and usually indicates severe
skew in the hash key rather than an undersized grant.

`SpilledThreadCount` against the plan's DOP tells you whether the spill was
uniform or a skew problem. One thread of eight spilling is a data distribution
problem, not a memory problem.

`GrantedMemoryKb` versus `UsedMemoryKb` on the spill detail elements shows
whether the grant was even close.

The root cause of most spills is an **underestimate** upstream: the memory grant
is sized from estimated rows. Fix the estimate and the spill usually disappears.
Granting more memory treats the symptom, and takes memory from other queries.

An **exchange spill** is different in character. It usually means a
`Repartition Streams` or `Gather Streams` deadlocked on its buffers and is
frequently caused by an order-preserving exchange under a merge join. It is
worth treating as its own problem rather than as "another spill."

## Missing statistics — `<ColumnsWithNoStatistics>`

The optimizer wanted a histogram on a column and there wasn't one. Every estimate
involving that column is a default guess (see `cardinality.md`).

Common causes: `AUTO_CREATE_STATISTICS` is off; the column is in a table variable;
the predicate is against a column of a type that cannot have statistics.

Not always actionable, but when present alongside a large cardinality skew on the
same table it is very likely the cause.

## Stale statistics — `<ColumnsWithStaleStatistics>`

Newer builds only. Says exactly what it says. Update the statistics and recompile.

## Memory grant — `<MemoryGrantWarning>`

Attributes: `GrantWarningKind`, `RequestedMemory`, `GrantedMemory`,
`MaxUsedMemory` (all KB).

`GrantWarningKind` is one of `ExcessiveGrant`, `GrantIncrease`, or
`UsedMoreThanGranted`.

An excessive grant is a server-wide problem, not a query problem. The query may
run fine while starving everything else. Look at
`GrantedMemory / MaxUsedMemory` — a ratio above about 10x deserves attention, and
above 50x is severe.

Grants are sized from estimated rows *and* estimated row size. A query selecting
`VARCHAR(MAX)` or wide `NVARCHAR` columns gets a grant based on **half the
declared maximum width**, not actual data length. `SELECT` of a `VARCHAR(8000)`
column that always contains 20 characters still reserves 4000 bytes per row. This
is why "just select fewer columns" is real advice and not a platitude.

## No join predicate — `<Warnings NoJoinPredicate="true">`

**Frequently a false alarm.** Read this one skeptically.

It genuinely fires for an accidental cross join. But it also fires when the
optimizer removed a redundant join predicate during simplification, and when an
`APPLY` correlates via an outer reference rather than a join predicate — both of
which are completely fine.

Three cases, each with a positive tell. Check them in this order. The digest's
`PREDICATES ON HOT / WARNED OPERATORS` section prints everything you need, and
`extract.py --node N` gives you the full detail for the joining operator.

**Correlated APPLY or outer reference (benign).** The join operator has an
`<OuterReferences>` list. Values are passed into the inner side rather than
compared by a predicate, so there is nothing for the warning to find.

**Transitive predicate elimination (benign, and the most common false alarm).**
This one has *neither* an `<OuterReferences>` list *nor* a join predicate, so it
superficially resembles the bad case. The tell is on the join's **inputs**, not
the join. If both children are independently filtered to the same constant — each
carrying a `Predicate` or `SeekPredicate` against the same literal — then the
optimizer proved the join predicate redundant and dropped it.

It happens any time you filter a join column by a constant:

```sql
SELECT TOP (1) c.Id
FROM dbo.Posts AS p
JOIN dbo.Comments AS c ON p.OwnerUserId = c.UserId
WHERE p.OwnerUserId = 22656;
```

Given `p.OwnerUserId = 22656` and `p.OwnerUserId = c.UserId`, it follows that
`c.UserId = 22656`. SQL Server pushes `= 22656` into both tables and discards the
join condition, which trips the warning. Both inputs are pinned to the same
value, so the "cross join" emits exactly the right rows. Confirm by checking that
the join's output row count did not multiply.

**Genuine cross join (bad).** No outer references, no join predicate, and the
inputs are *not* both pinned to the same constant. The output row count is
roughly the product of the input row counts. That multiplication is the
signature — an accidental cartesian product is loud.

## Unmatched indexes — `<Warnings UnmatchedIndexes="true">`

A filtered index could not be used because the query was parameterized and the
optimizer cannot prove at compile time that the parameter satisfies the filter
predicate. The child `<Parameterization>` elements name the index.

Fixes: add `OPTION (RECOMPILE)`, or write the filter predicate explicitly into
the query's `WHERE` clause so the optimizer can match it.

## Wait stats — `<WaitStats>` / `<Wait>`

Present in actual plans on reasonably modern builds. `WaitType`, `WaitTimeMs`,
`WaitCount`.

The ones worth naming:

- `RESOURCE_SEMAPHORE` — waiting for a memory grant. Somebody's grant is too big,
  possibly this query's.
- `PAGEIOLATCH_SH` — reading data pages from disk. Either the working set does not
  fit in memory or the query is reading far more than it needs.
- `CXPACKET` / `CXCONSUMER` — parallelism coordination. `CXCONSUMER` is generally
  benign. High `CXPACKET` with thread skew points at the skew, not at parallelism.
- `EXECSYNC` — threads blocked waiting for a serial phase of a parallel plan,
  classically the build of an **eager spool**. When `EXECSYNC` is near the top and
  an eager index spool is present, it is the same finding twice: the other threads
  sat idle while one built the spool. It corroborates, it is not a separate
  problem.
- `LCK_M_*` — blocking. The plan cannot tell you who blocked you.
- `SOS_SCHEDULER_YIELD` — CPU pressure, or a spinlock. Rarely the query's fault.
- `ASYNC_NETWORK_IO` — the client is not consuming results fast enough. Not a plan
  problem at all, and no amount of tuning fixes it.

**A serial plan showing heavy `CXPACKET` is not a contradiction.** If
`DegreeOfParallelism` is 0 or 1 and `NonParallelPlanReason` is set, but `CXPACKET`
dominates the waits and `UdfCpuTime` is large, the outer statement ran serially
while the scalar UDF's *own internal queries* each went parallel. The waits are
aggregated from those inner executions. Do not let the apparent contradiction talk
you out of a correct scalar-UDF diagnosis.

Waits are cumulative across threads, so a parallel query shows inflated wait
times. Compare against `QueryTimeStats/@ElapsedTime` before concluding anything.

## Spill occurred / spatial guess / full update for online index build

`<SpillOccurred>` appears in lightweight profiling output and only tells you that
*something* spilled. `<SpatialGuess>` and `<FullUpdateForOnlineIndexBuild>` are
informational and rarely the story.

## What is NOT in the warnings

Absence of a warning proves nothing. SQL Server does not warn about:

- Non-SARGable predicates (a function on a column)
- Key lookups, however many rows they run over
- Scalar UDFs running once per row
- Row goals gone wrong
- Eager index spools
- Nested loops joins over enormous outer inputs

Those you find by reading the plan.
