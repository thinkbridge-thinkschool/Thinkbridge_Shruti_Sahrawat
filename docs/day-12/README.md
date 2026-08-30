[← Back to full README](../../README.md)

## Day 12 — CQRS and Dapper

[`QuotesApi/Features/Collections/README.md`](../../QuotesApi/Features/Collections/README.md) — the Collections feature split into a write path through MediatR commands and the aggregate, and a read path projecting straight from the `DbContext` with `PreviewSize` pushed into SQL via a per-collection `ROW_NUMBER`.

[`QuotesApi/Features/Collections/DAPPER.md`](../../QuotesApi/Features/Collections/DAPPER.md) — the same read path in hand-written SQL, timed against the EF version, with the rule I would give a teammate for when to drop to Dapper.

Both implementations are held to the same contract by [`CollectionSummariesReadPathTests`](../../Quotes.Tests.Integration/CollectionSummariesReadPathTests.cs), which asserts they return identical results at four preview sizes, with an owner filter, for an empty collection, and when a previewed quote has been deleted underneath them. A faster query that answers a different question is not an optimisation.

---
