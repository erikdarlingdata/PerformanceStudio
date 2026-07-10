# Parallel plans

## CPU exceeding elapsed is normal

Eight threads working for one second burn eight CPU-seconds. `CpuTime` several
times `ElapsedTime` in `<QueryTimeStats>` is what parallelism *is*. It is not
evidence of a problem, and saying "CPU time is 5x elapsed time, something is
wrong" is a tell that you do not read plans.

What is worth noting is CPU *close to* elapsed in a plan running at DOP 8: the
plan went parallel and got no benefit, so it paid coordination costs for nothing.

## Thread skew

`<RunTimeCountersPerThread>` has one entry per thread. **Thread 0 is the
coordinator**, not a worker. It generally shows zero rows for most operators, and
for exchange operators its elapsed time is the wall clock of the whole branch.
Exclude it when assessing distribution.

Compare `ActualRows` across worker threads. Healthy is roughly even. Unhealthy:

- **One worker has everything, the rest have zero.** The branch is effectively
  serial and paid for parallelism anyway. Usually an exchange upstream failed to
  distribute, or the branch sits on the inner side of a nested loop and runs on
  one thread per outer row by design.
- **Distribution follows a skewed key.** `Repartition Streams` hashing on a
  column where one value dominates. Every row with that value lands on one
  thread. Repartition on a more selective column, or restructure.
- **Some workers idle entirely.** Fewer distinct hash values than threads.

Skew is the usual reason a parallel plan is slower than its serial version. It is
also the usual reason for a hash spill on one thread while the grant looks
generous: memory is divided evenly across threads, so the thread with all the
rows spills while the others sit on unused memory.

## Exchange operators

`Parallelism` is the physical operator. The logical operator says what it does:

- **Distribute Streams** — serial input, parallel output. One thread to many.
- **Repartition Streams** — parallel in, parallel out, rows reshuffled across
  threads. The `PartitionColumns` element says on what.
- **Gather Streams** — parallel input, serial output. Many threads to one.

Their timings are unreliable. An exchange accumulates the time it spends waiting
for whatever is downstream, so a spilling sort above a Gather Streams inflates the
gather's numbers. Never point at an exchange and say it is slow. See
`timing.md`.

An **order-preserving exchange** (`Gather Streams` with an `<OrderBy>`) is a merge
of sorted streams and is materially more expensive than an unordered one. It also
serializes on the slowest thread. Usually appears under a merge join or above a
sort feeding an `ORDER BY`. It is a common cause of exchange spills and, in older
builds, intra-query parallel deadlocks.

## Why a plan did not go parallel

`<QueryPlan NonParallelPlanReason="...">` tells you directly. Common values:

- `MaxDOPSetToOne`
- `EstimatedDOPIsOne` — the optimizer costed a parallel plan and did not want it
- `NoParallelPlansInDesktopOrExpressEdition`
- `NoParallelWithRemoteQuery`
- `TSQLUserDefinedFunctionsNotParallelizable` — a scalar UDF anywhere in the
  query forced the entire plan serial.
- `CouldNotGenerateValidParallelPlan` — **also very commonly a scalar UDF.**

Do not treat `TSQLUserDefinedFunctionsNotParallelizable` as the only UDF tell.
Real plans containing a serial-forcing scalar UDF frequently report
`CouldNotGenerateValidParallelPlan` instead. Whenever a large query is stubbornly
serial, check `<QueryTimeStats UdfCpuTime="..." />` regardless of which reason
string the plan gives. Before SQL Server 2019 this is a very common and very
expensive silent penalty.

A serial plan whose dominant wait is `CXPACKET` looks self-contradictory and is
not. The outer statement is serial; the UDF's internal queries went parallel, once
per row, and their waits aggregate into the plan. Large `UdfCpuTime` alongside
serial DOP and heavy `CXPACKET` is a scalar UDF, not a puzzle.

## DOP and the cost threshold

`<QueryPlan DegreeOfParallelism="N">` is the DOP actually used. A plan goes
parallel only if its *estimated* serial cost exceeds `cost threshold for
parallelism` (default 5, which was calibrated in 1997 and is far too low). So the
decision to parallelize rests entirely on estimates — an overestimate can produce
a parallel plan for a query that returns four rows.

Also: `DegreeOfParallelism` is what the optimizer requested. The query may have
run with fewer threads if none were available. Count distinct worker threads in
`RunTimeCountersPerThread` to see what it actually got.

## Serial zones inside a parallel plan

A parallel plan is not parallel everywhere. Between exchange operators the plan is
divided into branches, and each branch has its own DOP. Some operators force a
serial zone:

- Scalar UDFs (pre-2019)
- `TOP` in certain positions
- Backward scans
- Global aggregates above a `Gather Streams`
- Sequence functions like `ROW_NUMBER()` over an unpartitioned window, which
  must serialize

If the expensive operator sits in a serial zone, DOP is irrelevant to it and
raising MAXDOP will not help.
