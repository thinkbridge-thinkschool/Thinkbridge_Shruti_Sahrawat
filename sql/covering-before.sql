USE IndexLab;
GO

-- Remove the covering index from Day 8 part 1 so we start from a genuine
-- "before" state, then create a key-only index with no INCLUDE.
DROP INDEX IF EXISTS IX_QuoteEvents_AuthorId ON dbo.QuoteEvents;
GO
CREATE NONCLUSTERED INDEX IX_QuoteEvents_AuthorId_KeyOnly
    ON dbo.QuoteEvents (AuthorId);
GO

PRINT '=== BEFORE: index on (AuthorId) only - expect a Key Lookup ===';
GO
SET SHOWPLAN_TEXT ON;
GO
SELECT Id, AuthorName, Category, ViewCount FROM dbo.QuoteEvents WHERE AuthorId = 42;
GO
SET SHOWPLAN_TEXT OFF;
GO

SET STATISTICS IO ON;
GO
SELECT Id, AuthorName, Category, ViewCount FROM dbo.QuoteEvents WHERE AuthorId = 42;
GO
SET STATISTICS IO OFF;
GO
