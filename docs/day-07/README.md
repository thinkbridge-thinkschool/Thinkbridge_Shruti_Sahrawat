[← Back to full README](../../README.md)

## Day 7 — Joins and CTEs at depth

**Author quote counts with most-recent quote, in one statement**
[`sql/author-summary.sql`](../../sql/author-summary.sql) · [full notes and query plans](../../sql/README.md)

```sql
WITH ranked AS (
    SELECT Author, Text, CreatedAt,
           ROW_NUMBER() OVER (PARTITION BY Author ORDER BY CreatedAt DESC) AS rn,
           COUNT(*)     OVER (PARTITION BY Author)                         AS quote_count
    FROM Quotes
    WHERE IsDeleted = 0
)
SELECT Author, quote_count, Text AS most_recent_quote, CreatedAt AS most_recent_at
FROM ranked
WHERE rn = 1
ORDER BY quote_count DESC, Author
LIMIT 10;
```

The two required values pull in opposite directions: the count is an aggregate that collapses rows, while the most-recent quote is a column from one specific row that needs those rows kept. Window functions compute over a partition without collapsing it, so `ROW_NUMBER` picks the newest row per author and `COUNT(*) OVER` produces the count in the same pass.

**Why a CTE rather than a correlated subquery.** `EXPLAIN QUERY PLAN` shows the correlated version carrying a `CORRELATED SCALAR SUBQUERY` node with its own `SCAN q2` — the inner query depends on the outer row, so it re-scans and re-sorts once per author group. The CTE version has no `CORRELATED` node; it is nested co-routines pipelining from a single `SCAN Quotes`. Trade-off worth naming: the CTE version uses three `USE TEMP B-TREE` sorts against the correlated version's two, so it front-loads more sorting. On 23 rows neither is measurably slow — the CTE wins on how it scales, not on this dataset.

Same shape as the Day 5 N+1: one query per collection turned a request into six sequential round trips. A correlated subquery is that pattern moved inside the database engine.

Window functions and set operations are in the same folder: [`sql/WINDOW-FUNCTIONS.md`](../../sql/WINDOW-FUNCTIONS.md) covers `ROW_NUMBER`, `RANK`, `LAG` and a running total; [`sql/SET-OPERATIONS.md`](../../sql/SET-OPERATIONS.md) translates three business questions into `EXCEPT`, `INTERSECT` and `UNION`, with a note on why each operator was the right one.

---
