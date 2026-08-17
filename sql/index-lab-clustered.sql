USE IndexLab;
GO

CREATE CLUSTERED INDEX CIX_QuoteEvents_Id ON dbo.QuoteEvents (Id);
GO

SET STATISTICS IO ON;
GO
PRINT '=== AFTER CLUSTERED INDEX on (Id) ===';
GO

PRINT '--- Q1: point lookup by Id ---';
SELECT Id, AuthorName, Category, ViewCount FROM dbo.QuoteEvents WHERE Id = 57231;
GO

PRINT '--- Q2: range by AuthorId ---';
SELECT Id, AuthorName, CreatedAt FROM dbo.QuoteEvents WHERE AuthorId = 42;
GO

PRINT '--- Q3: filter by Category, ordered by CreatedAt ---';
SELECT TOP (20) Id, AuthorName, CreatedAt FROM dbo.QuoteEvents WHERE Category = 'modern' ORDER BY CreatedAt DESC;
GO
SET STATISTICS IO OFF;
GO
