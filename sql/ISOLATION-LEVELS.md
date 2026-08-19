# Day 9 — Isolation levels and the read anomalies

Two interactive `sqlcmd` sessions against SQL Server 2022 in Docker, reproducing each anomaly and then showing the isolation level that prevents it. Table:

```sql
CREATE TABLE dbo.Accounts (Id INT PRIMARY KEY, Owner NVARCHAR(50), Balance DECIMAL(10,2));
INSERT INTO dbo.Accounts VALUES (1, 'Alice', 1000.00), (2, 'Bob', 500.00);
```

Sessions were run in two terminals rather than orchestrated with `WAITFOR DELAY`, because the moment one session *blocks* on a lock the other holds is the thing worth seeing, and it is invisible in a scripted version. Each block was captured from `sys.dm_exec_requests` while it was happening.

## Summary

| Anomaly | Reproduced at | Lowest level that prevents it | Lock that does the preventing |
|---|---|---|---|
| Dirty read | READ UNCOMMITTED | **READ COMMITTED** | `LCK_M_S` — the reader waits for the writer |
| Non-repeatable read | READ COMMITTED | **REPEATABLE READ** | `LCK_M_X` — the writer waits for the reader |
| Phantom read | REPEATABLE READ | **SERIALIZABLE** | `RangeI-N` — an insert is blocked from a gap |

---

## 1. Dirty read

### Reproducing it at READ UNCOMMITTED

**Session A** — update without committing:

```sql
BEGIN TRANSACTION;
UPDATE dbo.Accounts SET Balance = 9999.00 WHERE Id = 1;
SELECT Id, Owner, Balance FROM dbo.Accounts WHERE Id = 1;
-- 1  Alice  9999.00
-- transaction deliberately left open
```

**Session B** — read the uncommitted value:

```sql
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT Id, Owner, Balance FROM dbo.Accounts WHERE Id = 1;
-- 1  Alice  9999.00
```

**Session A** — roll back:

```sql
ROLLBACK;
```

**Session B** — read again:

```sql
SELECT Id, Owner, Balance FROM dbo.Accounts WHERE Id = 1;
-- 1  Alice  1000.00
```

Session B read 9999.00 and could have acted on it. That value never existed in committed data — it was rolled back. Under `READ UNCOMMITTED` the reader takes no shared lock at all, so nothing stops it seeing another transaction's work in progress.

### Preventing it at READ COMMITTED

**Session A** — open transaction, uncommitted update again:

```sql
BEGIN TRANSACTION;
UPDATE dbo.Accounts SET Balance = 7777.00 WHERE Id = 1;
```

**Session B**:

```sql
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
SELECT Id, Owner, Balance FROM dbo.Accounts WHERE Id = 1;
-- hangs
```

Captured from a third session while it hung:

```
session_id  blocking_session_id  wait_type  wait_time  db
55          54                   LCK_M_S    54788      IndexLab
```

Session 55 is waiting for a shared lock held incompatible by session 54's exclusive lock. When Session A rolled back, Session B returned immediately with **1000.00** — never 7777.00. The dirty read is impossible because the reader is made to wait for committed data rather than reading through the lock.

---

## 2. Non-repeatable read

### Reproducing it at READ COMMITTED

**Session B** — open a transaction and read:

```sql
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
BEGIN TRANSACTION;
SELECT Id, Owner, Balance FROM dbo.Accounts WHERE Id = 1;
-- 1  Alice  1000.00
```

**Session A** — update and commit (autocommit):

```sql
UPDATE dbo.Accounts SET Balance = 2500.00 WHERE Id = 1;
```

**Session B** — same query, same transaction:

```sql
SELECT Id, Owner, Balance FROM dbo.Accounts WHERE Id = 1;
-- 1  Alice  2500.00
```

Nothing here is dirty — Session A committed, so 2500.00 is entirely valid. The problem is that one transaction read the same row twice and got two answers. Any logic that assumed those reads agreed is now wrong.

`READ COMMITTED` takes a shared lock for the duration of the read statement and releases it immediately, which prevents dirty reads but leaves the row free to change afterwards.

### Preventing it at REPEATABLE READ

**Session B**:

```sql
SET TRANSACTION ISOLATION LEVEL REPEATABLE READ;
BEGIN TRANSACTION;
SELECT Id, Owner, Balance FROM dbo.Accounts WHERE Id = 1;
-- 1  Alice  2500.00
```

**Session A**:

```sql
UPDATE dbo.Accounts SET Balance = 3333.00 WHERE Id = 1;
-- hangs
```

```
session_id  blocking_session_id  wait_type  wait_time
54          55                   LCK_M_X    92564
```

**The blocking direction has reversed.** In the dirty read case session 55 waited on 54 for `LCK_M_S`; here session 54 waits on 55 for `LCK_M_X`. `REPEATABLE READ` holds the reader's shared lock until the transaction ends, so the writer cannot acquire the exclusive lock it needs.

Session B's second read returned **2500.00** — repeatable. Session A's update completed the instant Session B committed.

---

## 3. Phantom read

### Reproducing it at REPEATABLE READ

**Session B** — a range query, not a single row:

```sql
SET TRANSACTION ISOLATION LEVEL REPEATABLE READ;
BEGIN TRANSACTION;
SELECT Id, Owner, Balance FROM dbo.Accounts WHERE Balance > 100;
-- 1  Alice  3333.00
-- 2  Bob     500.00
```

**Session A** — insert a row matching that range. This does **not** block:

```sql
INSERT INTO dbo.Accounts VALUES (3, 'Carol', 800.00);
```

**Session B** — identical query, same transaction:

```sql
SELECT Id, Owner, Balance FROM dbo.Accounts WHERE Balance > 100;
-- 1  Alice  3333.00
-- 2  Bob     500.00
-- 3  Carol   800.00
```

Two rows became three. Notice what `REPEATABLE READ` still delivered correctly: Alice and Bob did not change between the reads. The rows it had locked were protected.

**Why `REPEATABLE READ` cannot prevent this:** it locks the rows it read, and a row that does not exist yet cannot be locked. Row 3 was not in the result set of the first query, so there was nothing to hold a lock on, and the insert proceeded freely. That is the difference between the two anomalies — non-repeatable read is about an existing row *changing*, a phantom is about a new row *appearing*, and row-level locking only addresses the first.

### Preventing it at SERIALIZABLE

**Session B**:

```sql
SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
BEGIN TRANSACTION;
SELECT Id, Owner, Balance FROM dbo.Accounts WHERE Balance > 100;
-- 3 rows
```

**Session A**:

```sql
INSERT INTO dbo.Accounts VALUES (4, 'Dave', 900.00);
-- hangs
```

```
session_id  blocking_session_id  wait_type       wait_time
54          55                   LCK_M_RIn_NL    47522

request_session_id  resource_type  request_mode  request_status
54                  KEY            RangeI-N      WAIT
```

`RangeI-N` is a **Range-Insert** lock request, and it is qualitatively different from the previous two. `LCK_M_S` and `LCK_M_X` are locks on rows that exist. `SERIALIZABLE` takes key-range locks covering the *gaps between* rows — the space where a new row would go. Session A is not being refused access to a row; it is being refused permission to create one inside a locked range.

Session B's second read returned the same 3 rows. Dave's insert completed the moment Session B committed.

---

## The cost

Each level up holds locks longer, and every reproduction above showed that as a session hanging:

- `READ COMMITTED` blocked a reader for 54 seconds
- `REPEATABLE READ` blocked a writer for 92 seconds
- `SERIALIZABLE` blocked an insert for 47 seconds

Those numbers are only long because a human was typing. In production they would be milliseconds — but they are real waits, and they scale with concurrency. `SERIALIZABLE` prevents every anomaly here precisely because it locks the most, which is the same reason it is the wrong default for most applications.

SQL Server also offers `SNAPSHOT` isolation, which prevents all three anomalies without blocking by keeping row versions in tempdb. That trades lock contention for version-store overhead, and was out of scope for this exercise, which covers the `READ UNCOMMITTED` to `SERIALIZABLE` range the card specifies.