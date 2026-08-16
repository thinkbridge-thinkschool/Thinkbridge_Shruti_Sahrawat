-- Day 7 (window functions): per author, each quote with a running count
-- and the gap in days since that author's previous quote.
--
-- The named WINDOW clause defines the partition once so ROW_NUMBER and LAG
-- cannot drift apart. julianday() converts SQLite's text timestamps to a
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
