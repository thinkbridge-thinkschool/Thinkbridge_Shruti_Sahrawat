-- Day 26 follow-up: the TraceParent column for the live Azure SQL database.
--
-- Why this file exists instead of relying on the migration: exactly the same
-- reason sql/add-outbox-table.sql exists, and worth restating rather than
-- cross-referencing, because getting it wrong produces the same failure twice.
-- QuotesApi's Azure SQL path uses EnsureCreated(), not Migrate() - see
-- Program.cs's Database:SchemaBootstrap comment, and Days/day-24 Finding 17
-- for why Migrate() cannot be used there at all (every migration in
-- QuotesApi/Migrations was generated against SQLite). EnsureCreated() only
-- builds a schema from nothing and is a no-op against a database that already
-- has tables, so the AddOutboxTraceParent migration will never run against the
-- live database. Left alone, deploying Day 26's code would make every
-- POST /api/quotes fail on "Invalid column name 'TraceParent'" - the outbox
-- insert names a column the table does not have.
--
-- Matches QuotesApi/Migrations/20260909050544_AddOutboxTraceParent.cs and
-- Quotes.Tests.Integration/Migrations/SqlServer/20260909050603_AddOutboxTraceParent.cs
-- column-for-column; those are the source of truth if this ever needs
-- re-checking. Safe to run more than once.
--
-- Run once against the live database before (or with) the next deploy that
-- includes Day 26's code:
--   sqlcmd -S sql-quotes2-qvdk5l.database.windows.net -d quotesdb -G -i sql/add-outbox-traceparent.sql

IF NOT EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'[OutboxMessages]')
      AND name = 'TraceParent'
)
BEGIN
    -- Nullable, and it has to be. Every row already in this table was written
    -- before the column existed and has no trace context to record; a NOT NULL
    -- column would need a backfill value that would be a fabricated traceparent
    -- pointing at a request that never happened. NULL is the honest
    -- representation of "this row predates tracing", and the relay treats it
    -- as "publish normally, without stitching" rather than as an error.
    --
    -- 55 characters is the W3C traceparent's fixed length:
    --   version(2) - traceid(32) - spanid(16) - flags(2) + three hyphens.
    ALTER TABLE [OutboxMessages]
        ADD [TraceParent] nvarchar(55) NULL;

    PRINT 'Added OutboxMessages.TraceParent.';
END
ELSE
BEGIN
    PRINT 'OutboxMessages.TraceParent already exists; nothing to do.';
END
GO
