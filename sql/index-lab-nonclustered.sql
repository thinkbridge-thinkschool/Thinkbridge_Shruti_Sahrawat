USE IndexLab;
GO

-- NCI 1: covering index for the AuthorId lookup. INCLUDE carries the two
-- columns the query selects, so the index alone answers it - no lookup back
-- to the clustered index for each matching row.
CREATE NONCLUSTERED INDEX IX_QuoteEvents_AuthorId
    ON dbo.QuoteEvents (AuthorId)
    INCLUDE (AuthorName, CreatedAt);
GO

-- NCI 2: composite index matching the filter AND the sort order of Q3.
-- Category leads because it is the equality predicate; CreatedAt DESC follows
-- so the ORDER BY is satisfied by reading the index in order, with no sort.
CREATE NONCLUSTERED INDEX IX_QuoteEvents_Category_CreatedAt
    ON dbo.QuoteEvents (Category, CreatedAt DESC)
    INCLUDE (AuthorName);
GO

SET STATISTICS IO ON;
GO
PRINT '=== AFTER BOTH NON-CLUSTERED INDEXES ===';
GO

PRINT '--- Q1: point lookup by Id (clustered seek) ---';
SELECT Id, AuthorName, Category, ViewCount FROM dbo.QuoteEvents WHERE Id = 57231;
GO

PRINT '--- Q2: range by AuthorId (should use IX_QuoteEvents_AuthorId) ---';
SELECT Id, AuthorName, CreatedAt FROM dbo.QuoteEvents WHERE AuthorId = 42;
GO

PRINT '--- Q3: Category filter + CreatedAt sort (should use composite) ---';
SELECT TOP (20) Id, AuthorName, CreatedAt FROM dbo.QuoteEvents WHERE Category = 'modern' ORDER BY CreatedAt DESC;
GO

PRINT '--- Q4: index ignored - leading column not in the predicate ---';
SELECT TOP (20) Id, AuthorName, CreatedAt FROM dbo.QuoteEvents WHERE CreatedAt > DATEADD(DAY, -1, SYSUTCDATETIME());
GO
SET STATISTICS IO OFF;
GO
