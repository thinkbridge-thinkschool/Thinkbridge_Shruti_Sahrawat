# Day 8 — Clustered vs non-clustered indexes

100,000 rows in SQL Server 2022, measured with `SET STATISTICS IO ON` and `SET SHOWPLAN_TEXT ON`.

The Week-1 Quotes DB is SQLite, which has neither `SET STATISTICS IO` nor clustered indexes in the SQL Server sense — every SQLite table is a rowid or `WITHOUT ROWID` table, and there is no `CREATE CLUSTERED INDEX`. Since the exercise asks specifically for logical reads and an actual execution plan, this was run against SQL Server 2022 in Docker instead.

Scripts: [`index-lab-setup.sql`](index-lab-setup.sql), [`index-lab-baseline.sql`](index-lab-baseline.sql), [`index-lab-clustered.sql`](index-lab-clustered.sql), [`index-lab-nonclustered.sql`](index-lab-nonclustered.sql), [`index-lab-writes.sql`](index-lab-writes.sql), [`index-lab-plans.sql`](index-lab-plans.sql).

## Setup

The table is created deliberately as a **heap** — no primary key, so no clustered index — to give an honest baseline. `QuoteText` is padded to roughly 200 characters, because narrow rows pack many per 8 KB page and would understate the difference between a scan and a seek.

```sql
CREATE TABLE dbo.QuoteEvents (
    Id          INT            NOT NULL IDENTITY(1,1),
    AuthorId    INT            NOT NULL,
    AuthorName  NVARCHAR(200)  NOT NULL,
    Category    VARCHAR(20)    NOT NULL,
    QuoteText   NVARCHAR(1000) NOT NULL,
    CreatedAt   DATETIME2      NOT NULL,
    ViewCount   INT            NOT NULL
);
```

## Index DDL

```sql
CREATE CLUSTERED INDEX CIX_QuoteEvents_Id
    ON dbo.QuoteEvents (Id);

CREATE NONCLUSTERED INDEX IX_QuoteEvents_AuthorId
    ON dbo.QuoteEvents (AuthorId)
    INCLUDE (AuthorName, CreatedAt);

CREATE NONCLUSTERED INDEX IX_QuoteEvents_Category_CreatedAt
    ON dbo.QuoteEvents (Category, CreatedAt DESC)
    INCLUDE (AuthorName);
```

The `INCLUDE` columns on the first non-clustered index are the columns the query selects. Carrying them in the index leaf means the index alone answers the query, with no lookup back to the clustered index per matching row.

The second index leads with `Category` because that is the equality predicate, and follows with `CreatedAt DESC` so the `ORDER BY` is satisfied by reading the index in its stored order rather than sorting afterwards.

## Logical reads, before and after

| Query | Heap | + clustered | + both non-clustered |
|---|---|---|---|
| Q1 — point lookup `WHERE Id = 57231` | 6,250 | **3** | 3 |
| Q2 — range `WHERE AuthorId = 42` (184 rows) | 6,250 | 6,269 | **5** |
| Q3 — `WHERE Category = 'modern' ORDER BY CreatedAt DESC`, top 20 | 6,250 | 6,576 | **4** |
| Q4 — `WHERE CreatedAt > yesterday`, top 20 | — | — | 60 |

**The baseline is the interesting row.** All three queries cost exactly 6,250 reads on the heap, whether they returned 1 row, 184 rows, or a sorted top 20. A heap has one access path, so the shape of the query is irrelevant — SQL Server reads every page regardless of how selective the predicate is.

**The clustered index made two queries slightly worse.** Q2 went 6,250 → 6,269 and Q3 went 6,250 → 6,576. Both still scan everything, but they now scan the clustered index's leaf level, which carries B-tree overhead the raw heap did not have. A clustered index is not a free win for reads; it helps queries that use its key and mildly penalises those that do not.

**Q1 at 3 reads is the B-tree depth**, not a function of table size: root page, intermediate page, leaf page. The same seek on ten million rows would cost four.

## Execution plans

```
Q1  |--Clustered Index Seek(OBJECT:([QuoteEvents].[CIX_QuoteEvents_Id]),
        SEEK:([Id]=[@1]) ORDERED FORWARD)

Q2  |--Index Seek(OBJECT:([QuoteEvents].[IX_QuoteEvents_AuthorId]),
        SEEK:([AuthorId]=CONVERT_IMPLICIT(int,[@1],0)) ORDERED FORWARD)

Q3  |--Index Seek(OBJECT:([QuoteEvents].[IX_QuoteEvents_Category_CreatedAt]),
        SEEK:([Category]='modern') ORDERED FORWARD)

Q4  |--Index Scan(OBJECT:([QuoteEvents].[IX_QuoteEvents_AuthorId]),
        WHERE:([CreatedAt]>dateadd(day,(-1),sysutcdatetime())))
```

Two things the plans prove that the read counts only imply.

**Q2 has no Key Lookup operator.** That is the `INCLUDE` doing its job — without it, the index would find 184 matching rows and then perform 184 separate lookups into the clustered index to fetch `AuthorName` and `CreatedAt`, and the read count would be in the hundreds rather than 5.

**Q3 has no Sort operator.** `ORDERED FORWARD` on an index keyed `(Category, CreatedAt DESC)` means the rows arrive already in the requested order. The query reads 20 rows and stops. Had the index been keyed `(Category, CreatedAt ASC)` or `(CreatedAt, Category)`, a Sort would have appeared and the top-20 would first have had to materialise every 'modern' row.

**Q4 shows the leading-column rule.** `CreatedAt` is the second column of the composite index, so it cannot be seeked. What actually happened is more interesting than the index simply being unused: the optimizer chose `IX_QuoteEvents_AuthorId` and scanned it. That index is narrow, so scanning all of it costs 60 reads against the table's 6,250 — much better than a table scan, and still 20× worse than a seek. An index whose leading column is absent from the predicate does not become useless; it degrades into a cheap scan.

## The write-side cost

Identical 1,000-row insert, run twice:

| Indexes present | Logical reads |
|---|---|
| Clustered + 2 non-clustered | **12,731** |
| Clustered only | **531** |

**One line: the two non-clustered indexes made the same insert 24× more expensive in logical reads**, because each one is a separate sorted structure whose B-tree has to be navigated and updated for every row written.

That is the whole trade in one place. Those same two indexes took Q2 from 6,269 reads to 5 and Q3 from 6,576 to 4 — roughly a 1,300× read improvement — and cost 24× on writes. Which side matters is a property of the workload, not of the index: a reporting table that is written once and queried constantly should be heavily indexed, and a high-volume ingest table should not be.

## Running it

```bash
docker run -d --name sqlserver -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=<password>" \
  -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest

docker cp index-lab-setup.sql sqlserver:/tmp/setup.sql
docker exec sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "<password>" -C -i /tmp/setup.sql
```

Then the baseline, clustered, non-clustered, writes and plans scripts in that order.