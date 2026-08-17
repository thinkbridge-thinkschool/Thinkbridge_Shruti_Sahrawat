USE IndexLab;
GO

-- Same key column, but INCLUDE carries the three columns the query selects.
-- Key columns are sorted and seekable; included columns are payload in the
-- leaf. You key what you filter or sort on, and include what you select.
CREATE NONCLUSTERED INDEX IX_QuoteEvents_AuthorId_Covering
    ON dbo.QuoteEvents (AuthorId)
    INCLUDE (AuthorName, Category, ViewCount);
GO

PRINT '=== AFTER: same index plus INCLUDE - expect no Key Lookup ===';
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

PRINT '=== Index size comparison ===';
GO
SELECT i.name AS index_name,
       SUM(ps.used_page_count) AS pages,
       SUM(ps.row_count) AS rows_stored
FROM sys.dm_db_partition_stats ps
JOIN sys.indexes i ON i.object_id = ps.object_id AND i.index_id = ps.index_id
WHERE ps.object_id = OBJECT_ID('dbo.QuoteEvents')
GROUP BY i.name
ORDER BY pages DESC;
GO
