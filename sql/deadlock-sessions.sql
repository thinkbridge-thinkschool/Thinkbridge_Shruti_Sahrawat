-- Day 9 - Reproducing and resolving a deadlock
-- Two interactive sqlcmd sessions in separate terminals:
--   docker exec -it sqlserver /opt/mssql-tools18/bin/sqlcmd \
--       -S localhost -U sa -P "<password>" -C -d IndexLab
--
-- Statements are numbered in the order they must be typed. Running this file
-- as a script will not reproduce a deadlock - the interleaving is the point.

-- Enable the deadlock graph in the error log (either session, once)
DBCC TRACEON (1222, -1);


-- =====================================================================
-- REPRODUCING IT - opposite lock ordering
-- =====================================================================

-- [A1] Session A takes Alice
BEGIN TRANSACTION;
UPDATE dbo.Accounts SET Balance = Balance - 100 WHERE Id = 1;

-- [B1] Session B takes Bob. No conflict yet - different rows.
BEGIN TRANSACTION;
UPDATE dbo.Accounts SET Balance = Balance - 50 WHERE Id = 2;

-- [A2] Session A reaches for Bob. BLOCKS - B holds it.
UPDATE dbo.Accounts SET Balance = Balance + 100 WHERE Id = 2;

-- [B2] Session B reaches for Alice. Closes the cycle -> DEADLOCK.
--      One session receives Msg 1205 and is rolled back.
UPDATE dbo.Accounts SET Balance = Balance + 50 WHERE Id = 1;


-- Clean up in both sessions before the fix demo
IF @@TRANCOUNT > 0 ROLLBACK;


-- =====================================================================
-- THE FIX - consistent lock ordering: Id 1 always before Id 2
-- =====================================================================

-- [A3] Session A takes Alice first
BEGIN TRANSACTION;
UPDATE dbo.Accounts SET Balance = Balance - 100 WHERE Id = 1;

-- [B3] Session B also wants Alice first, so it BLOCKS rather than
--      taking Bob and creating a cycle. Ordinary queueing.
BEGIN TRANSACTION;
UPDATE dbo.Accounts SET Balance = Balance - 50 WHERE Id = 1;

-- [A4] Session A proceeds to Bob and commits, releasing B
UPDATE dbo.Accounts SET Balance = Balance + 100 WHERE Id = 2;
COMMIT;

-- [B4] Session B unblocks and completes. No Msg 1205.
UPDATE dbo.Accounts SET Balance = Balance + 50 WHERE Id = 2;
COMMIT;


-- =====================================================================
-- Reading the deadlock graph
-- =====================================================================
-- docker exec sqlserver bash -c \
--   "grep -A 60 'deadlock-list' /var/opt/mssql/log/errorlog | tail -70"