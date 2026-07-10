# Where the time actually went

The numbers on a plan operator do not mean what they look like they mean. This
file is the one that keeps you from being confidently, precisely wrong.

## Cost is not time

`EstimatedTotalSubtreeCost`, `StatementSubTreeCost`, and SSMS's "Query cost
(relative to batch): 97%" are all derived from the optimizer's row estimates and
a fixed hardware model from the late 1990s. They are **estimates in every plan,
including actual plans**. Nothing recomputes them after execution.

Consequences:

- The highest-cost operator is frequently not the slow one.
- An operator can show 0% cost and consume the entire runtime. A scalar UDF is
  the classic case: pre-2019 the optimizer costs it at essentially nothing while
  it runs once per row.
- Two plans' cost numbers are only comparable if their estimates were equally
  good, which is exactly what you are trying to determine.

Use cost to understand *why the optimizer chose what it chose*. Never use it to
say what was slow.

## Row mode reports cumulative time; batch mode does not

Every `RelOp` in an actual plan carries `<RunTimeInformation>` with one
`<RunTimeCountersPerThread>` per thread. The attributes you care about:

| Attribute | Meaning | Aggregate across threads by |
|---|---|---|
| `ActualRows` | rows emitted | **sum** |
| `ActualRowsRead` | rows examined before the predicate | **sum** |
| `ActualExecutions` | times this operator started | **sum** |
| `ActualCPUms` | CPU consumed | **sum** |
| `ActualElapsedms` | wall clock | **max** |
| `ActualLogicalReads` | pages read from buffer pool | **sum** |

Elapsed takes the max because threads run concurrently; the operator finished
when its slowest thread finished. CPU sums because every thread burned its own.

Now the trap. In **row mode**, `ActualElapsedms` and `ActualCPUms` are
**cumulative**: an operator's numbers include everything in its subtree. The
root node's elapsed time is therefore the whole query's elapsed time. Sorting
operators by raw `ActualElapsedms` sorts them by depth, and always crowns the
root. This is meaningless and it looks authoritative.

In **batch mode**, operators pipeline, and each reports its own standalone time.
No subtraction needed. Check `ActualExecutionMode` (falling back to
`EstimatedExecutionMode`) per operator — a single plan can mix both.

## Computing self time

For a row-mode operator:

```
self elapsed = ActualElapsedms - sum(effective elapsed of each child)
self CPU     = ActualCPUms     - sum(effective CPU of each child)
```

For a batch-mode operator, self time is the reported time. Do not subtract.

Three complications make "effective elapsed of each child" more than a lookup:

**Pass-through operators carry no runtime stats.** Compute Scalar is the usual
one. Its `ActualElapsedms` is absent or zero, not because it was instant but
because SQL Server does not record it. Subtracting zero for it makes its parent
absorb the whole subtree below. When a child has no runtime stats, look *through*
it and sum its children instead.

**Exchange operators lie.** `Parallelism` (Gather Streams, Repartition Streams,
Distribute Streams) accumulates time spent waiting on whatever is downstream. A
spilling sort above an exchange inflates the exchange's numbers. Worse, thread 0
of an exchange is the coordinator, and its elapsed time is the wall clock for the
entire parallel branch, not the operator's own work.

When computing an exchange's own time, ignore thread 0 and use the slowest worker
thread. When an exchange is a *child* and you need its contribution to a parent,
take the max over its children rather than its own reported number. Treat any
self time you compute for an exchange as advisory. Do not build a conclusion on
it.

**Parallel plans must subtract within a thread, never across threads.** For each
thread *t*:

```
self[t] = parent_elapsed[t] - sum(child_elapsed[t])
self    = max over t of self[t]
```

Subtracting an aggregate child total from an aggregate parent total mixes threads
that never ran together and produces garbage, frequently negative, which then
clamps to zero and hides the real hotspot.

`scripts/extract.py` implements all of this. Prefer it to doing the arithmetic
by hand.

## Batch-mode subtrees under a row-mode parent

If a row-mode operator sits above a contiguous batch-mode region, that region's
operators each reported standalone times. To subtract the region's contribution
from the row-mode parent, **sum** the batch operators' elapsed times across the
region, stopping at any `Parallelism` boundary. Taking just the topmost batch
operator's time undercounts and inflates the parent's apparent self time.

## Statement-level totals

`<QueryTimeStats>` on the `QueryPlan` element gives the authoritative totals:

- `ElapsedTime` — wall clock, milliseconds
- `CpuTime` — CPU, milliseconds
- `UdfElapsedTime` / `UdfCpuTime` — time inside scalar UDFs, when present

`UdfCpuTime` is worth checking whenever it exists. It is time the plan's
operators mostly do not attribute to themselves, and it is often the answer.

CPU far exceeding elapsed means parallelism, which is expected. Elapsed far
exceeding CPU means the query spent its life waiting — check `<WaitStats>`, and
`GrantWaitTime` in `<MemoryGrantInfo>`.

## Self times need not sum to the total in a parallel plan

In a **serial row-mode** plan, per-operator self elapsed times should roughly sum
to `QueryTimeStats/@ElapsedTime`. If they sum to several times the total, you
forgot to subtract children.

In a **parallel** plan they legitimately exceed it, sometimes substantially. Each
operator's elapsed is the max across its threads, and separate branches of the
plan run concurrently, so overlapping work is counted more than once. Two hot
operators showing 69.6s and 25.5s in a query that took 71.1s is not an arithmetic
error and does not mean you double-counted. Do not talk yourself out of a correct
answer because the numbers do not add up — in a parallel plan, they should not.

Self **CPU** behaves differently again: it sums across threads, so total CPU
routinely exceeds total elapsed by roughly the degree of parallelism. That is what
parallelism is.

## Sanity checks before you publish a number

- Is your top operator by self time the root node? Then you almost certainly
  computed cumulative time, not self time.
- Did you compute a negative self time and clamp it? That is a signal you
  subtracted across threads or through an exchange, not a signal the operator
  took zero time.
- Are you quoting a self elapsed time next to a *cumulative* CPU number? They are
  not comparable and the pairing is nonsense. Self elapsed and self CPU are both
  fine to quote, as long as you say which is which.
- Did you check `UdfCpuTime`? If it is large, the operators are lying to you by
  omission and the answer is the scalar UDF.
