[← Back to full README](../../README.md)

## Day 8 — Execution plans and indexes

[`sql/INDEXES.md`](../../sql/INDEXES.md) — a clustered and two non-clustered indexes over 100k generated rows, with `SET STATISTICS IO ON` logical-read counts before and after each one, and the write-side cost measured separately rather than asserted.

[`sql/COVERING-INDEXES.md`](../../sql/COVERING-INDEXES.md) — a query doing a key lookup, an index with `INCLUDE`d columns that eliminates it, and both plans side by side.

---
