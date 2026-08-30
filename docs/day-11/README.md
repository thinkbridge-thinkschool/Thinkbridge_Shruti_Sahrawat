[← Back to full README](../../README.md)

## Day 11 — Performance

[`perf/README.md`](../../perf/README.md) — a deliberately slow endpoint profiled under k6 load: p50/p99, the N+1 SQL it emits, and the execution plan.

[`perf/FIX.md`](../../perf/FIX.md) — the same endpoint after eliminating the N+1 and indexing `Author`. **p99 down 241x** against a 10x target, with before and after plans. Two overclaims in the original write-up were corrected afterwards; the corrections are in the commit history rather than quietly edited out.

---
