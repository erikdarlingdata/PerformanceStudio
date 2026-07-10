---
name: query-plan-analysis
description: Analyze a SQL Server execution plan (.sqlplan / showplan XML) and explain what is actually slow and why. Use whenever a user shares a query plan, asks why a query is slow, asks what an operator means, asks whether an index would help, or asks you to interpret estimated vs actual rows, spills, memory grants, or parallelism. Prevents the standard wrong conclusions - reading cost percentages as measurements, calling scans bad, or pasting missing-index DDL verbatim.
---

# Reading a SQL Server execution plan

A query plan tells you what SQL Server *decided* to do and, if it's an actual
plan, what happened when it did. Most bad plan analysis comes from confusing
those two things, or from reaching for the most visually obvious number rather
than the one that means something.

Work through the triage order below. It exists to establish ground truth before
you form an opinion, because almost every wrong conclusion about a plan comes
from forming the opinion first.

## Step 0: extract the plan before you read it

**Never read a `.sqlplan` file directly into context, and never `grep` one.**

- They are large. A trivial two-table join runs 120 KB; real plans run into
  megabytes. Reading one wastes your context and you will still miss things,
  because the interesting attributes are scattered across thousands of lines.
- They are usually **UTF-16**, so `grep`, `rg`, and friends silently match
  nothing. Finding no `PlanAffectingConvert` in a UTF-16 plan tells you
  nothing at all about whether the plan contains one.
- Some plans lie about their own encoding: SSMS writes UTF-16, but a plan
  that has been opened and re-saved is often UTF-8 bytes still declaring
  `encoding="utf-16"`. Strict XML parsers reject those.

Run the bundled extractor, which handles all of this and prints a digest ordered
to match the steps below. Use the **absolute path** to `extract.py` — your
working directory is not necessarily the skill root:

```
python /absolute/path/to/skills/query-plan-analysis/scripts/extract.py /path/to/plan.sqlplan
```

Add `--top 20` to widen the ranked sections on a large plan.

When you need more detail about one operator — its predicates, its seek keys, its
per-thread numbers — do **not** open the raw XML. Ask the extractor:

```
python .../scripts/extract.py /path/to/plan.sqlplan --node 16
```

That prints everything known about node 16: object and index, predicate, seek
predicate, outer references, output columns, estimates, actuals, and per-thread
stats. The digest already surfaces predicates for the hot and warned operators,
so you will often not need it.

If Python is genuinely unavailable, see `references/extracting-without-python.md`.

## Step 1: is this an actual plan or an estimated plan?

The digest says so explicitly. It decides what you are allowed to conclude.

An **estimated plan** contains no runtime information whatsoever. Every row
count in it is a guess, nothing ran, and you cannot say anything about what
was slow. You can reason about plan *shape*, index usage, obvious type
mismatches, and estimates that are self-evidently wrong. You cannot say "this
operator took the longest" because no operator took any time.

An **actual plan** has runtime counters. Now you can talk about time.

If someone asks why a query is slow and hands you an estimated plan, say that
you need an actual plan and explain how to capture one, rather than guessing
from cost percentages.

## Step 2: read the warnings SQL Server already gave you

These are free. They require no inference, and they are the highest
signal-to-noise content in the entire plan. The digest lists them all, tagged
with the node they came from.

Spills, implicit conversions, missing statistics, memory grant problems, and
no-join-predicate warnings all appear here. Read `references/warnings.md` for
what each one means and what to do about it. Several are more subtle than they
look, and at least one (no join predicate) is frequently a false alarm.

## Step 3: find where the time went, never where the cost went

This is the single most important rule in this document.

**Cost is always an estimate. Always.** The "Query cost (relative to batch):
97%" figure that SSMS puts at the top of a plan is computed from the
optimizer's guesses. It is present in actual plans. It is *not* a measurement,
it does not get updated with what really happened, and the highest-cost
operator in a plan is routinely not the slow one. A plan can show an operator
at 0% cost that consumed the entire query's runtime.

Never say "this operator is 97% of the cost, so it's the problem." Instead:

- Use the digest's **TOP OPERATORS BY SELF TIME** section.
- Or read `QueryTimeStats` for total elapsed and CPU, and per-operator
  `RunTimeCountersPerThread` for the breakdown.

Self time is not the number printed on the operator. In row mode, an operator's
elapsed and CPU are **cumulative** — they include everything beneath it — so
sorting operators by their raw `ActualElapsedms` always crowns the root node.
Batch mode reports each operator standalone. Exchange operators report times
that are nearly meaningless. Getting this wrong produces confident, precise,
completely inverted answers, which is the worst failure mode available to you.

Two things the operator list will not tell you, and both are commonly the answer:

- **`UdfCpuTime`.** A scalar UDF's time is not attributed to any operator. If
  `<QueryTimeStats>` reports a large `UdfCpuTime` or `UdfElapsedTime`, that is the
  query, whatever the operators say. The digest prints it as a percentage of
  elapsed and as a per-invocation cost.
- **Self times need not sum to the total.** In a parallel plan they legitimately
  exceed it, because elapsed is the max across threads and branches overlap. Do
  not second-guess a correct answer because the arithmetic looks off.

Read `references/timing.md` before you say anything about where time went. It
is short and it is the part most likely to embarrass you.

## Step 4: find where the estimates went wrong

Compare estimated rows to actual rows — but **per execution**, not in total.

On the inner side of a nested loop join, an operator runs once per outer row.
Showplan reports `EstimateRows` per execution and `ActualRows` as the sum
across all executions. Dividing is mandatory:

```
actual per execution = ActualRows / ActualExecutions
```

An operator showing "estimated 1 row, actual 4,000,000 rows" that ran 4,000,000
times estimated perfectly. Announcing a four-million-times underestimate there
is the most common way to be loudly wrong about a plan. The digest does this
division for you.

Large skew in either direction is worth investigating; see
`references/cardinality.md`, which also covers the optimizer's default guess
selectivities. When an estimate lands on exactly 30% of table cardinality, the
optimizer had no useful statistics and guessed — that is a fingerprint, and it
points at a different fix than a merely stale histogram.

## Step 5: check for skew before you trust a parallel plan

A parallel plan that ran DOP 8 but did all its work on one thread is a serial
plan that also paid for coordination. The digest reports per-thread row
distribution. Idle workers mean the rows did not distribute, and the usual
cause is upstream — an uneven partition key or a serial zone feeding the
exchange. See `references/parallelism.md`.

Also: high CPU relative to elapsed is normal and expected in a parallel plan.
It is not evidence of a problem by itself. Eight threads working for one second
burn eight CPU-seconds.

## Step 6: parameters, then memory grants

The digest prints `ParameterCompiledValue` against `ParameterRuntimeValue`. If
they differ, the plan was built for one value and executed with another. That
is parameter sniffing, sitting in plain sight, and it explains a large share of
"the same query is sometimes fast and sometimes slow" reports.

For memory grants, compare `GrantedMemory` to `MaxUsedMemory`. A query granted
9 GB that used 200 MB is stealing memory from everything else on the server and
may be waiting to get it (`GrantWaitTime`). A query that spilled despite a large
grant usually has skew, not a grant sizing problem.

## Step 7: only now, missing indexes

The `<MissingIndexes>` element is a hint about which predicates went unserved.
It is **not** DDL to hand to a user.

- The equality columns come out in arbitrary order. Key order is the single
  most important decision in index design and SQL Server does not make it.
- It ignores every index that already exists, so it will happily ask for a
  near-duplicate of one you have.
- It ignores the rest of the workload.
- Its `Impact` percentage is relative to the estimated cost of a plan you have
  already established is estimated.
- `INCLUDE` lists are frequently enormous, because it includes every column the
  query touches.

Read the request as "a seek on these columns would have helped." Then design the
index yourself, considering existing indexes and column selectivity. See
`references/indexes.md`.

## Things that are not evidence

Say none of these:

- **"There's a scan, that's bad."** A scan of a small table is optimal. A seek
  executed four million times is not. Scans of a 200-row lookup table are fine
  forever. Judge by rows touched and time spent, not operator name.
- **"The thick arrow is the problem."** Arrow thickness is row count, and in an
  estimated plan it is *guessed* row count. Rows are not time.
- **"Cost is 97% here."** See step 3.
- **"Key lookups are always bad."** A key lookup on 12 rows is nothing. On 12
  million it is the whole query. The count is what matters.
- **"The optimizer chose a bad plan."** Usually the optimizer chose the correct
  plan for the row counts it was given, and the row counts were wrong. Fix the
  estimate and the plan often fixes itself. Diagnose the estimate first.
- **"This query needs `OPTION (RECOMPILE)` / `MAXDOP 1` / a hint."** Hints
  suppress symptoms. Reach for them after you understand the cause, and say
  what the cause was.

## Writing the answer

Lead with what is actually slow and why, in one sentence, before any detail.
Quote the numbers you used and name their source, so the reader can check you:
name the operator, its node id, its self elapsed time, and the query's total
elapsed time, in that form. Precision about *which* number you are citing — self
time versus cumulative, per-execution versus total, estimated versus actual — is
the difference between a useful answer and a plausible one.

Then say what you ruled out, and why. "The memory grant used 17% of what it
asked for, so this is not a grant problem" is worth a line, because it stops the
reader from chasing it next.

If the plan does not support a conclusion, say so. "This is an estimated plan,
so I can tell you the shape looks wrong but not what was slow" is a good
answer. Inventing a bottleneck from cost percentages is not.

## Reference files

| File | Read it when |
|---|---|
| `references/timing.md` | Before making any claim about where time went |
| `references/cardinality.md` | Estimates disagree with actuals |
| `references/warnings.md` | The plan carries any warning |
| `references/parallelism.md` | The plan is parallel |
| `references/indexes.md` | You are about to recommend an index |
| `references/operators.md` | You need to explain a specific operator |
| `references/extracting-without-python.md` | Python is unavailable |
