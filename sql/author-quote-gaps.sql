-- Day 7 (window functions): per author, each quote with a running count
-- and the gap in days since that author's previous quote.
--
-- The named WINDOW clause defines the partition once so ROW_NUMBER, LAG and
-- SUM cannot drift apart. julianday() converts SQLite's text timestamps to a
-- floating-point day number, so subtracting two of them yields days directly.

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


-- Extended version: adds RANK and a SUM() OVER running total to contrast the
-- ranking functions. RANK orders by date(CreatedAt) rather than the full
-- timestamp, which deliberately creates ties - ranking by the full timestamp
-- would never tie and the contrast would be invisible.

SELECT
    Author,
    CreatedAt,
    ROW_NUMBER() OVER w                                        AS quote_number,
    RANK()       OVER (PARTITION BY Author ORDER BY date(CreatedAt)) AS rank_by_day,
    ROUND(julianday(CreatedAt) - julianday(LAG(CreatedAt) OVER w), 1) AS days_since_previous,
    SUM(1)       OVER w                                        AS running_total
FROM Quotes
WHERE IsDeleted = 0
  AND Author IN ('Grace Hopper', 'Donald Knuth')
WINDOW w AS (PARTITION BY Author ORDER BY CreatedAt)
ORDER BY Author, CreatedAt;
