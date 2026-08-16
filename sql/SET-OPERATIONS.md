# Day 7 — Set operations from a spec

Three business questions translated into SQL, using `UNION` / `INTERSECT` / `EXCEPT`.

## The schema decision

The questions reference tags and categories, which the Week-1 Quotes DB does not have — it holds `Quotes` and `Collections` only. Rather than add a migration whose only purpose is to serve a reporting query, the tag tables were created directly in `quotes.db` and deliberately kept out of the EF Core model. The API has no tagging feature, so tag entities in the application model would be dead weight.

EF ignores tables outside its model. That was verified rather than assumed: an unknown table was created, the app was started (`Now listening on: http://localhost:5067`), and the table dropped again.

Schema and seed data: [`tags-schema.sql`](tags-schema.sql).

```sql
CREATE TABLE Tags (
    Id       INTEGER PRIMARY KEY,
    Name     TEXT NOT NULL,
    Category TEXT NOT NULL CHECK (Category IN ('classic', 'modern')),
    UNIQUE (Name, Category)
);

CREATE TABLE QuoteTags (
    QuoteId INTEGER NOT NULL REFERENCES Quotes(Id),
    TagId   INTEGER NOT NULL REFERENCES Tags(Id),
    PRIMARY KEY (QuoteId, TagId)
);
```

`simplicity` exists in both categories deliberately — it makes the `UNION` question meaningful. Alan Turing and Grace Hopper are left untagged so the `EXCEPT` question has a non-empty answer.

---

## Q1 — Authors with quotes but no tags

**Operator: `EXCEPT`.** The question is a subtraction: every author, minus those who have at least one tagged quote. `EXCEPT` returns rows in the first result set that do not appear in the second, which is that subtraction directly.

```sql
SELECT DISTINCT Author FROM Quotes WHERE IsDeleted = 0
EXCEPT
SELECT DISTINCT q.Author
FROM Quotes q
JOIN QuoteTags qt ON qt.QuoteId = q.Id
WHERE q.IsDeleted = 0;
```

```
Author
------------
Alan Turing
Grace Hopper
```

A `LEFT JOIN ... WHERE TagId IS NULL` would return the same rows. `EXCEPT` was chosen because the question is phrased as set difference and the SQL reads the same way — the intent is on the surface rather than encoded in a NULL check.

---

## Q2 — Authors in both the 'classic' and 'modern' sets

**Operator: `INTERSECT`.** The question asks for membership of two sets at once, which is exactly what `INTERSECT` means: rows present in both result sets.

```sql
SELECT DISTINCT q.Author
FROM Quotes q
JOIN QuoteTags qt ON qt.QuoteId = q.Id
JOIN Tags t ON t.Id = qt.TagId
WHERE q.IsDeleted = 0 AND t.Category = 'classic'
INTERSECT
SELECT DISTINCT q.Author
FROM Quotes q
JOIN QuoteTags qt ON qt.QuoteId = q.Id
JOIN Tags t ON t.Id = qt.TagId
WHERE q.IsDeleted = 0 AND t.Category = 'modern';
```

```
Author
---------------
Donald Knuth
Edsger Dijkstra
Tony Hoare
```

The naive alternative — `WHERE Category = 'classic' AND Category = 'modern'` — returns nothing, because a single tag row has one category. The condition applies across an author's rows, not within one row, and `INTERSECT` is what expresses that.

---

## Q3 — The combined distinct tag list across two categories

**Operator: `UNION`.** Both categories' tags, deduplicated.

```sql
SELECT Name FROM Tags WHERE Category = 'classic'
UNION
SELECT Name FROM Tags WHERE Category = 'modern';
```

```
Name
-------------
concurrency
correctness
observability
pioneering
simplicity
```

Five rows, not six. `simplicity` exists in both categories, and the word *distinct* in the question is what makes `UNION` correct rather than `UNION ALL`:

```
UNION            UNION ALL
-------------    -------------
concurrency      correctness
correctness      simplicity
observability    pioneering
pioneering       concurrency
simplicity       observability
                 simplicity      <- duplicate kept
```

Worth noticing that `UNION` also returned the rows in alphabetical order while `UNION ALL` preserved source order. That is a side effect of how deduplication is implemented — the engine sorts or hashes to find duplicates — not a guarantee. Relying on it for ordering would be a bug waiting to happen; `ORDER BY` is the only thing that promises order.

The dedup is not free, either. `UNION ALL` is cheaper because it just concatenates. Here the question asked for a distinct list, so the cost is justified; where duplicates are impossible or acceptable, `UNION ALL` is the better default.

---

## Running it

```bash
cd QuotesApi
sqlite3 quotes.db ".read ../sql/tags-schema.sql"   # creates and seeds the tag tables
sqlite3 quotes.db ".read ../sql/set-operations.sql"
```