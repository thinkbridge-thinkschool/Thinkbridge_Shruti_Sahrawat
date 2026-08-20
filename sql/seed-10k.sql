-- Seed to 10,000 rows for the AsNoTracking benchmark.
-- Uses a recursive CTE to generate ids, cycling through a handful of authors
-- so the data has realistic repetition rather than 10,000 distinct authors.

WITH RECURSIVE counter(n) AS (
    SELECT 1
    UNION ALL
    SELECT n + 1 FROM counter WHERE n < 9977
)
INSERT INTO Quotes (Author, Text, CreatedAt, IsDeleted)
SELECT
    'Author ' || (n % 250),
    'Generated quote text number ' || n || '. Padding to give the row a realistic width for materialisation cost.',
    datetime('2026-01-01', '+' || (n % 365) || ' days'),
    0
FROM counter;
