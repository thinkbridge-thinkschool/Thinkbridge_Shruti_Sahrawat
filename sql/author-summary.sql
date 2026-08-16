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
