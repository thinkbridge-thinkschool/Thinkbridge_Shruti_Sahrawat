-- Day 20 follow-up: the OutboxMessages table for the live Azure SQL database.
--
-- Why this file exists instead of a migration: QuotesApi's Azure SQL path
-- uses EnsureCreated() (see Program.cs's Database:SchemaBootstrap comment),
-- not Migrate() - deliberately, because no SQL Server migration set ships
-- inside QuotesApi itself (only Quotes.Tests.Integration has one, for its own
-- throwaway databases). EnsureCreated() only builds a schema from nothing; it
-- is a no-op against a database that already has tables, which the live
-- Azure SQL database does since Day 19. Left alone, deploying Day 20's code
-- as-is would never create this table there, and the first POST /api/quotes
-- after deploy would 500 trying to insert into a table that doesn't exist.
--
-- This is hand-written, not exported from a migration, but it matches
-- QuotesApi/Migrations/20260901090500_AddOutbox.cs and
-- Quotes.Tests.Integration/Migrations/SqlServer/20260901090520_AddOutbox.cs
-- column-for-column and index-for-index - those are the source of truth if
-- this ever needs re-checking. Safe to run more than once.
--
-- Run once against the live database before (or immediately after) the next
-- deploy that includes Day 20's code:
--   sqlcmd -S sql-quotes2-qvdk5l.database.windows.net -d quotesdb -G -i sql/add-outbox-table.sql
-- (-G uses Azure AD auth; swap in whatever the runbook's usual connection
-- method is for this database.)

IF NOT EXISTS (
    SELECT 1 FROM sys.tables WHERE name = 'OutboxMessages'
)
BEGIN
    CREATE TABLE [OutboxMessages] (
        [Id]         int            NOT NULL IDENTITY(1,1),
        [MessageId]  nvarchar(200)  NOT NULL,
        [EventType]  nvarchar(64)   NOT NULL,
        [Payload]    nvarchar(max)  NOT NULL,
        [OccurredAt] datetime2      NOT NULL,
        [SentAt]     datetime2      NULL,
        CONSTRAINT [PK_OutboxMessages] PRIMARY KEY ([Id])
    );

    CREATE UNIQUE INDEX [IX_OutboxMessages_MessageId] ON [OutboxMessages] ([MessageId]);
    CREATE INDEX [IX_OutboxMessages_SentAt] ON [OutboxMessages] ([SentAt]);

    PRINT 'OutboxMessages created.';
END
ELSE
BEGIN
    PRINT 'OutboxMessages already exists - nothing to do.';
END
