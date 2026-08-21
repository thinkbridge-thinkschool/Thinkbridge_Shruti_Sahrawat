-- Day 11 - the index added to fix the profiled slow endpoint.
--
-- Applied directly to quotes.db rather than as an EF migration, consistent with
-- how the Day 7 tag tables were handled: this is a performance fix explored in
-- the profiling exercise rather than a schema change the application models.
-- In a real change this would belong in a migration so it deploys with the code.
--
-- It does two different jobs:
--   1. The N+1 inner query WHERE Author = ? goes from SCAN to SEARCH.
--   2. The GROUP BY Author aggregate loses its USE TEMP B-TREE FOR GROUP BY,
--      because the index already stores rows in author order.

CREATE INDEX IF NOT EXISTS IX_Quotes_Author ON Quotes(Author);
