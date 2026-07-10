# Estimates versus actuals

Most bad plans are good plans built on bad row counts. Diagnose the estimate
before you blame the optimizer.

## Always normalize per execution

Showplan reports these on different bases:

- `EstimateRows` — rows the optimizer expects **per execution** of the operator.
- `ActualRows` — rows actually emitted, **summed over every execution and every
  thread**.
- `ActualExecutions` — how many times the operator ran, summed over threads.

So the only valid comparison is:

```
actual per execution = ActualRows / ActualExecutions
skew = (actual per execution) / EstimateRows
```

An index seek on the inner side of a nested loop that shows `EstimateRows="1"`
and `ActualRows="4000000"` over `ActualExecutions="4000000"` estimated
**perfectly**. Reporting a 4-million-times underestimate there is the single most
common way to be loudly wrong about a plan, and it is instantly recognizable to
anyone who knows plans.

`EstimateExecutions` (or `EstimateRebinds` + `EstimateRewinds`) tells you what
the optimizer expected the execution count to be. When the row estimate per
execution is right but the *execution count* is badly wrong, the problem is on
the outer side of the join, not at the operator you are looking at.

## The optimizer's default guesses

When statistics are missing or unusable, the optimizer applies fixed selectivity
guesses. Estimates that land on these fractions of `TableCardinality` are a
fingerprint: they mean the optimizer had nothing to work with, and the fix is to
give it information, not to hint the query.

| Guess | Selectivity | Typical trigger |
|---|---|---|
| Equality | 30% | `col = @var` with no usable statistic; table variable |
| Inequality | 10% | `col > @var`, `col < @var` |
| `LIKE` / `BETWEEN` | 9% | `col LIKE @pattern` |
| Compound predicate | ~16.4% | two filters combined under the new CE |
| Multiple inequalities | 1% | `col > @a AND col < @b` |

The new cardinality estimator (`CardinalityEstimationModelVersion` 120 and
above) also uses *exponential backoff* when combining predicates, rather than the
legacy CE's independence assumption. Two predicates each 10% selective estimate
at roughly 3.2% under the old model and roughly 5.6% under the new one. Neither
is right when the columns are correlated.

`scripts/extract.py` flags estimates that match these fingerprints.

## Common causes, in rough order of frequency

**Table variables.** Before SQL Server 2019 (or under compatibility level below
150), a table variable always estimates 1 row regardless of contents, because it
has no statistics. Deferred compilation in 2019+ fixes the *initial* estimate but
not subsequent modifications. A table variable feeding a nested loop join is the
classic cause of a plan that works on a laptop and dies in production.

**Implicit conversion on a predicate.** If the column's type must be converted to
match the literal or parameter, the optimizer cannot use the histogram and falls
back to a guess. See `warnings.md`.

**A function wrapping the column.** `WHERE YEAR(OrderDate) = 2024` is not
SARGable. The optimizer has no statistic on `YEAR(OrderDate)` and guesses. Rewrite
as a range: `WHERE OrderDate >= '2024-01-01' AND OrderDate < '2025-01-01'`.

**Stale statistics.** The histogram describes data that no longer exists. Check
whether the estimate matches what the table *used to* look like. Auto-update
triggers at roughly `SQRT(1000 * rowcount)` modifications for large tables, which
on a billion-row table is a million rows — a table can drift very far while
statistics still count as fresh.

**Correlated predicates.** `WHERE City = 'Chicago' AND State = 'IL'` — the
optimizer multiplies the two selectivities as if independent, and badly
underestimates. Multi-column statistics or a filtered index can help.

**Parameter sniffing.** The estimate is correct for the value the plan was
compiled with and wrong for the value it ran with. Check
`ParameterCompiledValue` against `ParameterRuntimeValue`. This is not a bad
estimate in the usual sense — the histogram was fine — so the fix is different.

**Local variables and `OPTIMIZE FOR UNKNOWN`.** A local variable's value is not
known at compile time, so the optimizer uses the density vector (average rows per
distinct value) rather than the histogram. This produces a stable, mediocre
estimate rather than a good one.

**Multi-statement table-valued functions.** Fixed estimate of 1 row before 2014,
100 rows from 2014 on, regardless of what the function returns. Interleaved
execution in 2017+ fixes this for the first execution. Inline TVFs do not have the
problem at all.

## Which direction matters

**Underestimate** — the optimizer thinks fewer rows than there are. It picks
nested loops, key lookups, serial plans, and too-small memory grants. The result
is spills, and joins that repeat millions of times. Underestimates usually
present as a query that is much slower than it should be.

**Overestimate** — the optimizer thinks more rows than there are. It picks hash
joins, sorts, parallelism, and enormous memory grants. The result is memory
pressure, `RESOURCE_SEMAPHORE` waits, and concurrency collapse across the
instance. Overestimates often present as one query that is fine alone and a
server that falls over under load.

An overestimate can also cause an eager index spool: the optimizer decides that
building a temporary index at runtime will pay for itself over the many rows it
expects, then builds it for a handful of rows. If you see one, look for a large
overestimate on the operator feeding it.

## What to do about it

In order of preference:

1. Fix the estimate. Update statistics, remove the implicit conversion, make the
   predicate SARGable, replace the table variable with a temp table.
2. Give the optimizer information it does not have. Multi-column statistics,
   filtered statistics, a filtered index.
3. Restructure the query so the bad estimate does not matter. Materializing an
   intermediate result into a `#temp` table gives the optimizer real statistics
   at the cost of a write.
4. Only then, hints. And when you use one, say what estimate it is compensating
   for.
