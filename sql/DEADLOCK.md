# Day 9 — Reproducing and resolving a deadlock

Two `sqlcmd` sessions against SQL Server 2022 in Docker, forcing a classic two-resource deadlock, capturing the graph via trace flag 1222, then fixing it with consistent lock ordering.

Table: `dbo.Accounts` with `Id 1 (Alice)` and `Id 2 (Bob)`. Scripts: [`deadlock-sessions.sql`](deadlock-sessions.sql).

```sql
DBCC TRACEON (1222, -1);   -- deadlock graph to the error log
```

## Reproducing it

The cause is **opposite lock ordering**. Each session takes one row, then reaches for the other's.

| Step | Session A | Session B |
|---|---|---|
| 1 | `BEGIN TRAN; UPDATE ... WHERE Id = 1;` | |
| 2 | | `BEGIN TRAN; UPDATE ... WHERE Id = 2;` |
| 3 | `UPDATE ... WHERE Id = 2;` → blocks | |
| 4 | | `UPDATE ... WHERE Id = 1;` → **deadlock** |

After step 2 there is no conflict — different rows, both succeed immediately. Step 3 blocks: A holds Alice and wants Bob, which B holds. Step 4 closes the cycle: B holds Bob and now wants Alice, which A holds. Neither can release without first acquiring, so neither ever will.

## The victim message

```
Msg 1205, Level 13, State 51, Server 6329cc0e51f1, Line 1
Transaction (Process ID 57) was deadlocked on lock resources with another
process and has been chosen as the deadlock victim. Rerun the transaction.
```

Session A completed normally. SQL Server does not resolve deadlocks by waiting them out — it cannot, since they never resolve — so it detects the cycle and kills one participant to free the other.

## The deadlock graph

From the error log with trace flag 1222 on. Stack frames omitted; the process and resource lists are the substance.

```
deadlock-list
 deadlock victim=processd14ba04e8

  process-list
   process id=processd14ba04e8 spid=57 lockMode=X waitresource=KEY: 5:72057594046251008 (8194443284a0)
            transactionname=user_transaction isolationlevel=read committed (2)
    inputbuf: UPDATE dbo.Accounts SET Balance = Balance + 50 WHERE Id = 1;

   process id=processd162ecca8 spid=56 lockMode=X waitresource=KEY: 5:72057594046251008 (61a06abd401c)
            transactionname=user_transaction isolationlevel=read committed (2)
    inputbuf: UPDATE dbo.Accounts SET Balance = Balance + 100 WHERE Id = 2;

  resource-list
   keylock objectname=IndexLab.dbo.Accounts indexname=PK__Accounts__3214EC07C1C74C1F mode=X
    owner-list
     owner  id=processd162ecca8 mode=X        <- spid 56 holds this key
    waiter-list
     waiter id=processd14ba04e8 mode=X        <- spid 57 wants it

   keylock objectname=IndexLab.dbo.Accounts indexname=PK__Accounts__3214EC07C1C74C1F mode=X
    owner-list
     owner  id=processd14ba04e8 mode=X        <- spid 57 holds this key
    waiter-list
     waiter id=processd162ecca8 mode=X        <- spid 56 wants it
```

The resource list is where the cycle is undeniable. Two key locks on the same index, `PK__Accounts__3214EC07C1C74C1F`. On the first, 56 is the owner and 57 the waiter. On the second, those roles are exactly reversed. Each `inputbuf` shows the statement that was blocked, which identifies the offending pair directly.

Both processes show `logused=240`, so the usual tiebreak — kill the transaction with least log to roll back — did not decide it; SQL Server fell back to another criterion and chose spid 57.

## The fix: consistent lock ordering

Both sessions acquire `Id = 1` before `Id = 2`, always, regardless of which they logically "need" first.

| Step | Session A | Session B |
|---|---|---|
| 1 | `BEGIN TRAN; UPDATE ... WHERE Id = 1;` | |
| 2 | | `BEGIN TRAN; UPDATE ... WHERE Id = 1;` → **blocks** |
| 3 | `UPDATE ... WHERE Id = 2;` `COMMIT;` | |
| 4 | | unblocks, `UPDATE ... WHERE Id = 2;` `COMMIT;` |

Both transactions completed. No `Msg 1205`, no victim, no rollback.

**Why it works, in one line:** a deadlock requires a cycle in the wait-for graph, and a cycle requires at least one session to acquire locks in a different order from another — so if every session takes resources in the same order, the graph is always acyclic and no cycle can form.

Worth being precise about what the fix does and does not do. Session B **still blocked** at step 2 — consistent ordering does not remove contention, it converts a cycle into a queue. B waited for A rather than deadlocking with it, and then proceeded. One transaction waits; none gets killed.

## Running it

Two terminals:

```bash
docker exec -it sqlserver /opt/mssql-tools18/bin/sqlcmd \
    -S localhost -U sa -P "<password>" -C -d IndexLab
```

Statements must be typed in the numbered order in [`deadlock-sessions.sql`](deadlock-sessions.sql) — running that file as a script will not reproduce anything, since the interleaving is the point.

Reading the graph afterwards:

```bash
docker exec sqlserver bash -c "grep -A 60 'deadlock-list' /var/opt/mssql/log/errorlog | tail -70"
```