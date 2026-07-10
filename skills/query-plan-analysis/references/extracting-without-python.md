# Getting at a plan without the extractor

Use `scripts/extract.py` when you can. This file is the fallback.

## The encoding problem comes first

`.sqlplan` files written by SSMS are **UTF-16**. Text tools see a null byte
between every character:

- `grep PlanAffectingConvert plan.sqlplan` matches nothing, even when the plan is
  full of implicit conversions. It reports no match rather than an error, so a
  negative result is worthless.
- `head`, `cat`, and `wc -l` produce garbage.

Convert first:

```
iconv -f UTF-16 -t UTF-8 plan.sqlplan > plan.utf8.xml
```

Some plans are UTF-8 bytes that still declare `encoding="utf-16"` in the XML
prolog, because someone opened and re-saved them. `iconv` will fail on those and
strict XML parsers will reject them. Check the first bytes: if the file starts
with the literal characters `<?xml`, it is already UTF-8 and needs no conversion
regardless of what the declaration claims.

## Once it is UTF-8

The namespace is `http://schemas.microsoft.com/sqlserver/2004/07/showplan` on
every showplan ever emitted, despite the year. XPath tools need it bound; `grep`
does not care.

Establish plan type first:

```
grep -c "RunTimeInformation" plan.utf8.xml
```

Zero means an estimated plan and you cannot discuss timing at all.

Then the warnings, which are the highest-value content:

```
grep -o "PlanAffectingConvert[^/]*" plan.utf8.xml
grep -o "SpillToTempDb[^/]*" plan.utf8.xml
grep -o "ColumnsWithNoStatistics" plan.utf8.xml
grep -o "NoJoinPredicate=\"[^\"]*\"" plan.utf8.xml
```

Statement totals:

```
grep -o "QueryTimeStats[^/]*" plan.utf8.xml
grep -o "MemoryGrantInfo[^/]*" plan.utf8.xml
```

Parameters, for sniffing:

```
grep -o "ParameterCompiledValue=\"[^\"]*\"" plan.utf8.xml
grep -o "ParameterRuntimeValue=\"[^\"]*\"" plan.utf8.xml
```

## Per-operator numbers by hand

This is where it gets painful, and why the extractor exists. To rank operators by
self time you need, for every `RelOp`:

- its `NodeId`, `PhysicalOp`, `LogicalOp`
- every child `RunTimeCountersPerThread` (`ActualElapsedms`, `ActualCPUms`,
  `ActualRows`, `ActualExecutions`, `Thread`)
- its parent-child structure, to subtract children

and then you must apply the row-mode/batch-mode/exchange rules in `timing.md`.
Doing this with `grep` is not realistic on a plan of any size, and doing it
approximately produces answers that are wrong in the specific way that sounds
authoritative.

If Python is genuinely unavailable, prefer to say what the warnings and
statement-level `QueryTimeStats` support, and state plainly that you cannot rank
operators by self time without parsing the plan properly. That is a better answer
than a confident ranking of cumulative times.

## What never to do

- Do not read the whole plan into context. A trivial plan is 120 KB.
- Do not rank operators by `EstimatedTotalSubtreeCost` and present it as what was
  slow. It is an estimate, in every plan.
- Do not rank operators by raw `ActualElapsedms` in a row-mode plan. That ranks
  them by depth and always puts the root first.
- Do not conclude anything from a `grep` that returned no matches until you have
  confirmed the file is not UTF-16.
