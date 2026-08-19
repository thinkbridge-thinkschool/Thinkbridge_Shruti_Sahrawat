-- Day 9 - Isolation levels and the read anomalies
-- Two interactive sqlcmd sessions, run in separate terminals:
--   docker exec -it sqlserver /opt/mssql-tools18/bin/sqlcmd \
--       -S localhost -U sa -P "<password>" -C -d IndexLab
--
-- Statements are numbered in the order they must be typed. Do not run this
-- file as a script - the point is the interleaving.

-- Setup (either session)
CREATE TABLE dbo.Accounts (Id INT PRIMARY KEY, Owner NVARCHAR(50), Balance DECIMAL(10,2));
INSERT INTO dbo.Accounts VALUES (1, 'Alice', 1000.00), (2, 'Bob', 500.00);


-- =====================================================================
-- 1. DIRTY READ - reproduced at READ UNCOMMITTED
-- =====================================================================

-- [A1] Session A: update, do not commit
BEGIN TRANSACTION;
UPDATE dbo.Accounts SET Balance = 9999.00 WHERE Id = 1;

-- [B1] Session B: reads 9999.00 - uncommitted data
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT Id, Owner, Balance FROM dbo.Accounts WHERE Id = 1;

-- [A2] Session A: roll back - the value B read never existed
ROLLBACK;

-- [B2] Session B: reads 1000.00
SELECT Id, Owner, Balance FROM dbo.Accounts WHERE Id = 1;


-- 1b. PREVENTED at READ COMMITTED

-- [A3] Session A
BEGIN TRANSACTION;
UPDATE dbo.Accounts SET Balance = 7777.00 WHERE Id = 1;

-- [B3] Session B: BLOCKS on LCK_M_S instead of reading dirty data
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
SELECT Id, Owner, Balance FROM dbo.Accounts WHERE Id = 1;

-- [A4] Session A: releases B, which then returns 1000.00
ROLLBACK;


-- =====================================================================
-- 2. NON-REPEATABLE READ - reproduced at READ COMMITTED
-- =====================================================================

-- [B4] Session B: first read, 1000.00
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
BEGIN TRANSACTION;
SELECT Id, Owner, Balance FROM dbo.Accounts WHERE Id = 1;

-- [A5] Session A: update and commit (autocommit) - does not block
UPDATE dbo.Accounts SET Balance = 2500.00 WHERE Id = 1;

-- [B5] Session B: same query, same transaction, now 2500.00
SELECT Id, Owner, Balance FROM dbo.Accounts WHERE Id = 1;
COMMIT;


-- 2b. PREVENTED at REPEATABLE READ

-- [B6] Session B: first read, 2500.00
SET TRANSACTION ISOLATION LEVEL REPEATABLE READ;
BEGIN TRANSACTION;
SELECT Id, Owner, Balance FROM dbo.Accounts WHERE Id = 1;

-- [A6] Session A: BLOCKS on LCK_M_X - blocking direction has reversed
UPDATE dbo.Accounts SET Balance = 3333.00 WHERE Id = 1;

-- [B7] Session B: second read still 2500.00, then release A
SELECT Id, Owner, Balance FROM dbo.Accounts WHERE Id = 1;
COMMIT;


-- =====================================================================
-- 3. PHANTOM READ - reproduced at REPEATABLE READ
-- =====================================================================

-- [B8] Session B: range query, 2 rows
SET TRANSACTION ISOLATION LEVEL REPEATABLE READ;
BEGIN TRANSACTION;
SELECT Id, Owner, Balance FROM dbo.Accounts WHERE Balance > 100;

-- [A7] Session A: insert into the range - does NOT block, because
--      REPEATABLE READ locks rows that exist, and row 3 did not
INSERT INTO dbo.Accounts VALUES (3, 'Carol', 800.00);

-- [B9] Session B: same query, now 3 rows - Carol is the phantom
SELECT Id, Owner, Balance FROM dbo.Accounts WHERE Balance > 100;
COMMIT;


-- 3b. PREVENTED at SERIALIZABLE

-- [B10] Session B: range query, 3 rows
SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
BEGIN TRANSACTION;
SELECT Id, Owner, Balance FROM dbo.Accounts WHERE Balance > 100;

-- [A8] Session A: BLOCKS on LCK_M_RIn_NL, lock mode RangeI-N.
--      A key-range lock covers the gap where the new row would go.
INSERT INTO dbo.Accounts VALUES (4, 'Dave', 900.00);

-- [B11] Session B: still 3 rows, no phantom. COMMIT releases A.
SELECT Id, Owner, Balance FROM dbo.Accounts WHERE Balance > 100;
COMMIT;


-- =====================================================================
-- Observing the blocks (run from a third session while one hangs)
-- =====================================================================

SELECT session_id, blocking_session_id, wait_type, wait_time
FROM sys.dm_exec_requests
WHERE blocking_session_id <> 0;

SELECT request_session_id, resource_type, request_mode, request_status
FROM sys.dm_tran_locks
WHERE request_status = 'WAIT';