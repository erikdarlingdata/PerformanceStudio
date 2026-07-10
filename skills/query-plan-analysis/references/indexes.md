# Indexes

## The missing index request is a hint, not a script

`<MissingIndexes>` records which predicates the optimizer could not serve with an
existing index. That is genuinely useful information. The `CREATE INDEX`
statement people paste out of it is not a recommendation, and shipping it
verbatim is the fastest way to look like you do not know what you are doing.

What is wrong with it:

- **Equality column order is arbitrary.** Showplan emits them in column-id order,
  not selectivity order. Key column order is the most consequential decision in
  index design. SQL Server does not make it for you.
- **Existing indexes are ignored.** The optimizer asks for what would help *this*
  query, with no regard for the eleven indexes already on the table. Following the
  requests one at a time produces near-duplicate indexes that all have to be
  maintained on every write.
- **The `INCLUDE` list is every other column the query touches.** On a wide
  `SELECT` this can be most of the table, producing an index nearly as large as
  the table itself.
- **`Impact` is a percentage of an estimated cost.** It is an estimate of the
  improvement to an estimate. It is not a promise, and a 99% impact figure on a
  query with bad cardinality means nothing.
- **It never suggests a clustered index, a filtered index, or a columnstore**,
  and it never suggests *dropping* anything.

## Reading it properly

Take the column groups as evidence about the query's access pattern:

- `EQUALITY` columns — predicates of the form `col = something`. These belong at
  the front of the key.
- `INEQUALITY` columns — `>`, `<`, `BETWEEN`, `LIKE 'x%'`. These belong after all
  the equality columns. Everything after the first inequality column in a key can
  only be used as a residual filter, not for seeking, so there is rarely a reason
  to have more than one.
- `INCLUDE` columns — columns the query needs to return but does not filter or
  join on. Adding them avoids a key lookup at the cost of index size.

Order the equality columns by selectivity, most selective first, unless you know
the workload wants otherwise. Then check what indexes already exist: an existing
index whose key is a prefix of what you want should usually be *modified* rather
than joined by a new one.

## Key lookups

A `Key Lookup` (or `RID Lookup` on a heap) means a nonclustered index found the
rows but did not contain every column the query needed, so SQL Server went back
to the clustered index once per row.

Whether it matters is entirely about the row count. Look at `ActualExecutions` on
the lookup: that is how many times it ran. Twelve is free. Twelve million is the
whole query.

Two fixes:

- Add the missing columns to the nonclustered index's `INCLUDE` list. Cheap,
  effective, and grows the index.
- Return fewer columns. Frequently the query is selecting columns nobody uses.

A lookup with a **residual predicate** — a `Predicate` element on the lookup
operator itself — is worse than it looks. It means the filter could not be applied
until after the lookup, so SQL Server paid for the lookup on rows it then
discarded. Compare `ActualRows` on the lookup against `ActualRows` on its parent.
The gap is wasted work, and moving that predicate's column into the index key
eliminates it.

## Scans are not the enemy

A `Clustered Index Scan` is a table scan. That is not automatically a defect.

- Scanning a 200-row table is optimal. An index seek would be slower.
- Scanning is correct when the query genuinely needs most of the table.
- The optimizer chooses a scan over a seek when it estimates the seek plus
  lookups would cost more. Above roughly 1% selectivity for a wide row, that is
  usually correct arithmetic.

What makes a scan a problem is a *selective predicate* that the scan had to apply
itself. Check the operator's `ActualRowsRead` against `ActualRows`. Reading 17
million rows to emit 200 means the predicate ran as a filter instead of a seek.
*That* is worth fixing — and the fix is an index, or making the predicate
SARGable, not "avoiding the scan."

`ActualRowsRead` is only present when a predicate is pushed into the scan. Its
absence, with `ActualRows` equal to `TableCardinality`, means the query really did
want the whole table.

## Non-SARGable predicates

SQL Server cannot seek on an expression it does not have an index for. These force
a scan and, worse, a cardinality guess:

| Non-SARGable | Rewrite |
|---|---|
| `WHERE YEAR(d) = 2024` | `WHERE d >= '20240101' AND d < '20250101'` |
| `WHERE ISNULL(c, 0) = 5` | `WHERE c = 5` (handle NULL separately if needed) |
| `WHERE c LIKE '%foo'` | Full-text, or store a reversed computed column |
| `WHERE CAST(c AS VARCHAR) = '5'` | Fix the type on the other side |
| `WHERE c + 0 = 5` | `WHERE c = 5` |
| `WHERE ABS(c) = 5` | `WHERE c IN (5, -5)` |

A leading wildcard `LIKE '%foo'` cannot seek under any index and never will.
`LIKE 'foo%'` seeks fine.

An `OR` across different columns often defeats index usage entirely; the optimizer
may expand it into a union of seeks, or may give up and scan. Check whether it
did. `UNION ALL` of two well-indexed queries is frequently faster and always more
predictable.

## Eager index spool

An `Index Spool` with logical operation `Eager Spool` means the optimizer decided
to build a temporary index in tempdb, at runtime, because no suitable permanent
index existed. It builds it from scratch on every execution.

This is the optimizer telling you, in the loudest voice it has, exactly which
index to create. Take the spool's `SeekPredicate` for the key columns and its
`OutputList` for the includes. The digest prints both under `PREDICATES ON HOT /
WARNED OPERATORS`; `extract.py --node N` prints the full detail.

An eager spool is *eager*: it fully materializes before returning any rows.
Combined with a large overestimate on its input, it can dominate a query's
runtime entirely — building a temporary index over millions of rows to serve a
handful of seeks.

**An eager index spool usually suppresses the missing-index request.** The plan
will contain no `<MissingIndexes>` element, and the request will not appear in
`sys.dm_db_missing_index_details` either. So "SQL Server didn't suggest an index"
is not evidence that no index is needed — when a spool is present, it is evidence
of the opposite. Nothing will prompt you. You have to recognize the spool as the
request.

In a parallel plan the spool build runs on a single thread while the others block
on it, which surfaces as `EXECSYNC` near the top of `<WaitStats>`. That wait and
the spool are one finding, not two.

Eager spools also appear legitimately in update plans, where they protect against
the Halloween Problem. Those are not defects.
