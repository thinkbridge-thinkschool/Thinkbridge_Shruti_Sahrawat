# Day 7 — Window functions

Per author, each quote with a running count and the gap in days since that author's previous quote.

Database: the Week-1 Quotes DB (`QuotesApi/quotes.db`, SQLite 3.53.4), 23 rows across 10 authors.

## The query

See [`author-quote-gaps.sql`](author-quote-gaps.sql):

```sql
SELECT
    Author,
    CreatedAt,
    Text,
    ROW_NUMBER() OVER w AS quote_number,
    ROUND(
        julianday(CreatedAt) - julianday(LAG(CreatedAt) OVER w),
        1
    ) AS days_since_previous
FROM Quotes
WHERE IsDeleted = 0
WINDOW w AS (PARTITION BY Author ORDER BY CreatedAt)
ORDER BY Author, CreatedAt;
```

Three things worth noting about how it is written.

**The named `WINDOW` clause.** `ROW_NUMBER` and `LAG` need identical partitioning and ordering — if they drift apart the running count and the gap describe different sequences. Defining `w` once makes that impossible rather than merely unlikely. SQLite supports named windows; not every engine does.

**`ORDER BY CreatedAt` ascending**, unlike the previous exercise which used `DESC` to find the most recent quote. A running count has to start at an author's first quote, so the ordering inside the window is the opposite of the one used to pick a latest row.

**`julianday()`** converts SQLite's text timestamps to a floating-point day number, so subtracting two of them yields days directly. SQLite has no date type, so without this the subtraction would be meaningless string arithmetic.

`LAG` returns NULL for the first row in each partition, which is correct: an author's first quote has no previous quote, and NULL is the honest answer rather than zero.

## Sample rows

```
Author             CreatedAt                    quote_number  days_since_previous
Ada Lovelace       2026-02-28 09:00:00          1             NULL
Ada Lovelace       2026-05-15 09:00:00          2             76.0
Alan Turing        2026-08-13 11:03:22.430241   1             NULL
Barbara Liskov     2026-02-14 09:00:00          1             NULL
Barbara Liskov     2026-07-19 09:00:00          2             155.0
Donald Knuth       2026-01-22 09:00:00          1             NULL
Donald Knuth       2026-05-30 09:00:00          2             128.0
Donald Knuth       2026-08-01 09:00:00          3             63.0
Edsger Dijkstra    2026-01-05 09:00:00          1             NULL
Edsger Dijkstra    2026-03-11 09:00:00          2             65.0
Edsger Dijkstra    2026-06-02 09:00:00          3             83.0
Grace Hopper       2026-08-13 11:32:57.9568063  1             NULL
Grace Hopper       2026-08-13 11:37:07.415714   2             0.0
Grace Hopper       2026-08-13 11:39:27.4084536  3             0.0
Grace Hopper       2026-08-13 11:41:51.629081   4             0.0
Grace Hopper       2026-08-13 11:44:54.0655007  5             0.0
Leslie Lamport     2026-06-25 09:00:00          1             NULL
Leslie Lamport     2026-07-30 09:00:00          2             35.0
Margaret Hamilton  2026-04-08 09:00:00          1             NULL
Shruti             2026-08-13 12:08:23.2171901  1             NULL
Shruti             2026-08-13 14:02:04.9092021  2             0.1
Tony Hoare         2026-03-03 09:00:00          1             NULL
Tony Hoare         2026-08-10 09:00:00          2             160.0
```

The running count restarts at 1 for every author, and each author's first row is NULL rather than 0.

## What the output exposed

Grace Hopper's four gaps all read `0.0`, and Shruti's reads `0.1`. Those rows were created minutes apart during earlier API testing, so the column is not wrong — the rounding to one decimal place is discarding the interval. Expressing the same subtraction in minutes shows what is actually there:

```
Author        CreatedAt                    days_rounded  minutes
Grace Hopper  2026-08-13 11:32:57.9568063  NULL          NULL
Grace Hopper  2026-08-13 11:37:07.415714   0.0           4.2
Grace Hopper  2026-08-13 11:39:27.4084536  0.0           2.3
Grace Hopper  2026-08-13 11:41:51.629081   0.0           2.4
Grace Hopper  2026-08-13 11:44:54.0655007  0.0           3.0
```

The window function is doing its job correctly in both cases. The unit choice is what makes the result readable or misleading, and "0.0 days" reads as "no time passed" when four minutes did. On a dataset where authors post several times an hour, a days column would report every gap as zero while looking entirely plausible.

## Running it

```bash
cd QuotesApi
sqlite3 quotes.db ".read ../sql/author-quote-gaps.sql"
```