# Day 8 — Covering indexes and included columns

Taking a query that performs a Key Lookup, adding `INCLUDE`d columns to eliminate it, and proving it from the execution plan.

Same environment as [`INDEXES.md`](INDEXES.md): SQL Server 2022 in Docker, `dbo.QuoteEvents` with 102,000 rows. Scripts: [`covering-before.sql`](covering-before.sql), [`covering-after.sql`](covering-after.sql).

## The query

```sql
SELECT Id, AuthorName, Category, ViewCount
FROM dbo.QuoteEvents
WHERE AuthorId = 42;
```

189 rows match.

## Before — index on the key column only

```sql
CREATE NONCLUSTERED INDEX IX_QuoteEvents_AuthorId_KeyOnly
    ON dbo.QuoteEvents (AuthorId);
```

This index stores `AuthorId` and, implicitly, the clustered key `Id`. The query also selects `AuthorName`, `Category` and `ViewCount`, none of which the index holds — so for every matching row SQL Server has to go back to the clustered index and fetch them.

```
|--Nested Loops(Inner Join, OUTER REFERENCES:([Uniq1001], [QuoteEvents].[Id], [Expr1004])
                WITH UNORDERED PREFETCH)
     |--Index Seek(OBJECT:([QuoteEvents].[IX_QuoteEvents_AuthorId_KeyOnly]),
            SEEK:([AuthorId]=(42)) ORDERED FORWARD)
     |--Clustered Index Seek(OBJECT:([QuoteEvents].[CIX_QuoteEvents_Id]),
            SEEK:([Id]=[Id] AND [Uniq1001]=[Uniq1001]) LOOKUP ORDERED FORWARD)
```

**Logical reads: 589.**

Read bottom-up. The `Index Seek` finds the 189 matching rows cheaply. The `Clustered Index Seek` marked `LOOKUP` executes **once per row** to fetch the missing columns. The `Nested Loops` drives the second from the first. 189 lookups at roughly three reads each accounts for almost all of the 589.

`WITH UNORDERED PREFETCH` is worth noticing: the optimizer recognises the pattern is expensive and issues read-aheads to soften it. That is the engine compensating for a problem the index shape created.

## After — the same key column, with `INCLUDE`

```sql
CREATE NONCLUSTERED INDEX IX_QuoteEvents_AuthorId_Covering
    ON dbo.QuoteEvents (AuthorId)
    INCLUDE (AuthorName, Category, ViewCount);
```

```
|--Index Seek(OBJECT:([QuoteEvents].[IX_QuoteEvents_AuthorId_Covering]),
       SEEK:([AuthorId]=(42)) ORDERED FORWARD)
```

**Logical reads: 5.**

That single line is the entire plan. The Key Lookup and the Nested Loops that drove it are both gone — the index now carries every column the query needs, so it is answered from the index alone.

## The delta

| | Before | After |
|---|---|---|
| Logical reads | 589 | **5** |
| Plan operators | 3 (Nested Loops, Index Seek, Key Lookup) | **1** (Index Seek) |
| Clustered index accesses | 189 | **0** |

**118× fewer logical reads** for an identical query and result set. The only change is which columns the index carries.

## Key columns versus included columns

The distinction matters and is easy to get wrong. Key columns are sorted, so they can be seeked and can satisfy an `ORDER BY`. Included columns are not sorted — they are payload stored in the index leaf. The rule that follows: **key what you filter or sort on, include what you merely select.**

Putting `ViewCount` in the key would have widened every level of the B-tree, not just the leaf, for no benefit — nothing filters or sorts on it.

## What it costs

Index sizes from `sys.dm_db_partition_stats`:

| Index | Pages |
|---|---|
| `CIX_QuoteEvents_Id` (the table itself) | 6,407 |
| `IX_QuoteEvents_AuthorId_Covering` | 712 |
| `IX_QuoteEvents_Category_CreatedAt` | 701 |
| `IX_QuoteEvents_AuthorId_KeyOnly` | 197 |

The covering index is **3.6× larger** than the key-only version — 712 pages against 197 — because each leaf row now carries `AuthorName` (up to 200 characters), `Category` and `ViewCount` instead of just a row pointer. That is roughly 11% of the table duplicated.

So the honest statement of the trade is: 118× fewer reads on this query, at 3.6× the index storage, and a correspondingly wider structure to maintain on every insert, update and delete. The Day 8 write test measured that side directly — two non-clustered indexes made a 1,000-row insert 24× more expensive in logical reads.

A covering index is also **query-specific by construction**. Add one column to the `SELECT` list and the lookup returns. That is its main fragility: it optimises exactly the query it was designed for and silently stops covering the moment that query changes.

## Running it

```bash
docker cp covering-before.sql sqlserver:/tmp/before.sql
docker exec sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "<password>" -C -i /tmp/before.sql -y 0

docker cp covering-after.sql sqlserver:/tmp/after.sql
docker exec sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "<password>" -C -i /tmp/after.sql -y 0
```