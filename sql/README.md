# Day 7 — Joins and CTEs at depth

Each author with their quote count and their most-recent quote, in one statement, using a CTE and window functions rather than a correlated subquery.

Database: the Week-1 Quotes DB (`QuotesApi/quotes.db`, SQLite 3.53.4). `seed.sql` adds 15 rows across 8 additional authors so the result set is worth reading; the original 8 rows were mostly duplicates of a single quote.

## The query

See [`author-summary.sql`](author-summary.sql):

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

The difficulty is that the two required values pull in opposite directions. The count is an aggregate, which collapses rows; the most-recent quote is a column from one specific row, which needs those rows kept. `GROUP BY` gives the first and destroys the second. `MAX(CreatedAt)` gives the timestamp but not the text that belongs to it.

Window functions resolve that by computing over a partition without collapsing it. `PARTITION BY Author` restarts numbering per author, `ORDER BY CreatedAt DESC` puts the newest first, and `WHERE rn = 1` keeps only that row. `COUNT(*) OVER (PARTITION BY Author)` produces the count in the same pass, so no separate `GROUP BY` or join back to an aggregate is needed.

`WHERE IsDeleted = 0` is deliberate: soft-deleted quotes should count neither toward the total nor as an author's most recent.

## Result (top 10)

| Author | quote_count | most_recent_quote | most_recent_at |
|---|---|---|---|
| Grace Hopper | 5 | The most dangerous phrase is we have always done it this way. | 2026-08-13 11:44:54 |
| Donald Knuth | 3 | An algorithm must be seen to be believed. | 2026-08-01 09:00:00 |
| Edsger Dijkstra | 3 | The question of whether machines can think is about as relevant as whether submarines can swim. | 2026-06-02 09:00:00 |
| Ada Lovelace | 2 | That brain of mine is something more than merely mortal. | 2026-05-15 09:00:00 |
| Barbara Liskov | 2 | I did not set out to be a pioneer. | 2026-07-19 09:00:00 |
| Leslie Lamport | 2 | If you are thinking without writing, you only think you are thinking. | 2026-07-30 09:00:00 |
| Shruti | 2 | Traces make the invisible visible | 2026-08-13 14:02:04 |
| Tony Hoare | 2 | There are two ways of constructing a software design. | 2026-08-10 09:00:00 |
| Alan Turing | 1 | Machines take me by surprise with great frequency. | 2026-08-13 11:03:22 |
| Margaret Hamilton | 1 | There was no choice but to be pioneers. | 2026-04-08 09:00:00 |

## Why a CTE here rather than a correlated subquery

**One line:** the correlated subquery re-executes once per author group, so the work grows with the number of authors, while the CTE with window functions computes both values in a single pass over the table.

`EXPLAIN QUERY PLAN` shows it rather than asserting it.

**Correlated subquery version:**

```
QUERY PLAN
|--SCAN q1
|--USE TEMP B-TREE FOR GROUP BY
`--CORRELATED SCALAR SUBQUERY 1
   |--SCAN q2
   `--USE TEMP B-TREE FOR ORDER BY
```

`CORRELATED SCALAR SUBQUERY` containing its own `SCAN q2` is the tell: the inner query depends on the outer row, so it runs again for every author, and each run is a full scan plus a sort. Ten authors means ten extra scans; ten thousand authors means ten thousand.

**CTE + window function version:**

```
QUERY PLAN
|--CO-ROUTINE ranked
|  |--CO-ROUTINE (subquery-3)
|  |  |--CO-ROUTINE (subquery-4)
|  |  |  |--SCAN Quotes
|  |  |  `--USE TEMP B-TREE FOR ORDER BY
|  |  |--SCAN (subquery-4)
|  |  `--USE TEMP B-TREE FOR ORDER BY
|  `--SCAN (subquery-3)
|--SCAN ranked
`--USE TEMP B-TREE FOR ORDER BY
```

No `CORRELATED` node anywhere. The nested co-routines are pipelined stages streaming rows from a single `SCAN Quotes` at the base.

**The honest trade-off:** the CTE version uses three `USE TEMP B-TREE` sorts against the correlated version's two, so it does more sorting work up front and holds more in memory. On 23 rows neither is measurably slow, and the correlated version might even win. The CTE wins on how it scales — its cost grows with row count, while the correlated version's grows with row count multiplied by author count.

The same shape caused a real problem on Day 5: an N+1 in the collections endpoint, where one query per collection turned a 4.45s request into six sequential database round trips. A correlated subquery is that pattern moved inside the database engine.

## Running it

```bash
cd QuotesApi
sqlite3 quotes.db ".read ../sql/seed.sql"            # optional, adds sample authors
sqlite3 quotes.db ".read ../sql/author-summary.sql"
```