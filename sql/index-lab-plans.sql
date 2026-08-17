USE IndexLab;
GO
-- Recreate the two non-clustered indexes dropped by the write test.
CREATE NONCLUSTERED INDEX IX_QuoteEvents_AuthorId
    ON dbo.QuoteEvents (AuthorId) INCLUDE (AuthorName, CreatedAt);
CREATE NONCLUSTERED INDEX IX_QuoteEvents_Category_CreatedAt
    ON dbo.QuoteEvents (Category, CreatedAt DESC) INCLUDE (AuthorName);
GO

SET SHOWPLAN_TEXT ON;
GO
SELECT Id, AuthorName, Category, ViewCount FROM dbo.QuoteEvents WHERE Id = 57231;
GO
SELECT Id, AuthorName, CreatedAt FROM dbo.QuoteEvents WHERE AuthorId = 42;
GO
SELECT TOP (20) Id, AuthorName, CreatedAt FROM dbo.QuoteEvents WHERE Category = 'modern' ORDER BY CreatedAt DESC;
GO
SELECT TOP (20) Id, AuthorName, CreatedAt FROM dbo.QuoteEvents WHERE CreatedAt > DATEADD(DAY, -1, SYSUTCDATETIME());
GO
SET SHOWPLAN_TEXT OFF;
GO
