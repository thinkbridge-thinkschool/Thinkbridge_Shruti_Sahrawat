USE IndexLab;
GO
SET STATISTICS IO ON;
GO

PRINT '=== WRITE COST: INSERT 1000 rows WITH all three indexes ===';
GO
INSERT INTO dbo.QuoteEvents (AuthorId, AuthorName, Category, QuoteText, CreatedAt, ViewCount)
SELECT TOP (1000)
    ABS(CHECKSUM(NEWID())) % 500 + 1,
    CONCAT('Author ', ABS(CHECKSUM(NEWID())) % 500 + 1),
    CASE ABS(CHECKSUM(NEWID())) % 3 WHEN 0 THEN 'classic' WHEN 1 THEN 'modern' ELSE 'contemporary' END,
    REPLICATE(N'Quote text padding to make rows realistically wide. ', 4),
    DATEADD(MINUTE, -(ABS(CHECKSUM(NEWID())) % 525600), SYSUTCDATETIME()),
    ABS(CHECKSUM(NEWID())) % 10000
FROM sys.all_objects a CROSS JOIN sys.all_objects b;
GO

PRINT '=== Now drop the two non-clustered indexes and insert 1000 more ===';
GO
DROP INDEX IX_QuoteEvents_AuthorId ON dbo.QuoteEvents;
DROP INDEX IX_QuoteEvents_Category_CreatedAt ON dbo.QuoteEvents;
GO

PRINT '=== WRITE COST: INSERT 1000 rows with CLUSTERED INDEX ONLY ===';
GO
INSERT INTO dbo.QuoteEvents (AuthorId, AuthorName, Category, QuoteText, CreatedAt, ViewCount)
SELECT TOP (1000)
    ABS(CHECKSUM(NEWID())) % 500 + 1,
    CONCAT('Author ', ABS(CHECKSUM(NEWID())) % 500 + 1),
    CASE ABS(CHECKSUM(NEWID())) % 3 WHEN 0 THEN 'classic' WHEN 1 THEN 'modern' ELSE 'contemporary' END,
    REPLICATE(N'Quote text padding to make rows realistically wide. ', 4),
    DATEADD(MINUTE, -(ABS(CHECKSUM(NEWID())) % 525600), SYSUTCDATETIME()),
    ABS(CHECKSUM(NEWID())) % 10000
FROM sys.all_objects a CROSS JOIN sys.all_objects b;
GO
SET STATISTICS IO OFF;
GO
