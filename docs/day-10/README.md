[← Back to full README](../../README.md)

## Day 10 — EF Core internals

[`Quotes.Benchmark/README.md`](../../Quotes.Benchmark/README.md) — change tracker versus `AsNoTracking` over 10k rows, measured with BenchmarkDotNet rather than a stopwatch, so the allocation numbers mean something. Includes the case where `AsNoTracking` is the wrong choice.

[`Quotes.Benchmark/PROJECTIONS.md`](../../Quotes.Benchmark/PROJECTIONS.md) — the SQL EF generates for a whole-entity query, the leaner SQL after projecting to a DTO, and one accidental client-side evaluation caught and fixed.

---
