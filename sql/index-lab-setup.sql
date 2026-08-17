IF DB_ID('IndexLab') IS NOT NULL
BEGIN
    ALTER DATABASE IndexLab SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE IndexLab;
END
GO
CREATE DATABASE IndexLab;
GO
USE IndexLab;
GO

-- Deliberately created as a HEAP: no PRIMARY KEY, so no clustered index.
-- This gives an honest "before" baseline to measure the clustered index against.
CREATE TABLE dbo.QuoteEvents (
    Id          INT           NOT NULL IDENTITY(1,1),
    AuthorId    INT           NOT NULL,
    AuthorName  NVARCHAR(200) NOT NULL,
    Category    VARCHAR(20)   NOT NULL,
    QuoteText   NVARCHAR(1000) NOT NULL,
    CreatedAt   DATETIME2     NOT NULL,
    ViewCount   INT           NOT NULL
);
GO

-- 100,000 rows generated from system tables cross-joined to reach the row count.
INSERT INTO dbo.QuoteEvents (AuthorId, AuthorName, Category, QuoteText, CreatedAt, ViewCount)
SELECT TOP (100000)
    ABS(CHECKSUM(NEWID())) % 500 + 1,
    CONCAT('Author ', ABS(CHECKSUM(NEWID())) % 500 + 1),
    CASE ABS(CHECKSUM(NEWID())) % 3 WHEN 0 THEN 'classic' WHEN 1 THEN 'modern' ELSE 'contemporary' END,
    REPLICATE(N'Quote text padding to make rows realistically wide. ', 4),
    DATEADD(MINUTE, -(ABS(CHECKSUM(NEWID())) % 525600), SYSUTCDATETIME()),
    ABS(CHECKSUM(NEWID())) % 10000
FROM sys.all_objects a
CROSS JOIN sys.all_objects b;
GO

SELECT COUNT(*) AS row_count FROM dbo.QuoteEvents;
GO
