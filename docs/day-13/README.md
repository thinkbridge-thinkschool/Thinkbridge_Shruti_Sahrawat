[← Back to full README](../../README.md)

## Day 13 — Signals, zoneless, standalone

[`quotes-ui/`](../../quotes-ui) — an Angular 21 client for the Week-1 Quotes API.
One screen: a paged, filtered list read from `GET /api/quotes`.

The exercise was to *direct* an agent rather than hand-type components, so the
artefacts are three: [`BRIEF.md`](../../quotes-ui/BRIEF.md) is the prompt,
[`src/app/`](../../quotes-ui/src/app) is what came back, and
[`VERIFICATION.md`](../../quotes-ui/VERIFICATION.md) is what happened when it was run.

Standalone throughout with no `NgModule`, `inject()` rather than constructor
injection, `signal` / `computed` / `linkedSignal` / `effect` for state, and
`@if` / `@for` with `track` / `@switch` in the template. Zoneless — which in
Angular 21 means *not* adding a provider, since it is now the default and
`provideZoneChangeDetection()` is the opt-out.

**Three bugs, all found by running it rather than reading it.** `status()` on
`HttpResourceRef` is the resource lifecycle, not the HTTP status — `statusCode()`
is, and it is `undefined` rather than `0` when nothing answered. A `?? 0`
fallback on `totalCount` collapsed the pager to "Page 3 of 1 (0 quotes total)"
during every refetch, because the count was *late* rather than absent;
`linkedSignal` exists for exactly that. And a request that never settles renders
as `loading` indefinitely — the dev proxy refused a connection and never
answered, so the error branch never ran at all. `loading`, `error` and `ready`
are not exhaustive; **"never answered" is a fourth state**, and that was a gap in
the brief, not only in the code.

The screen also demonstrates the limit of client-side filtering against a paged
API: with 10,000 rows and a page size of 100, filtering for an author who exists
but is not on the current page reports no matches. Accurate about the page,
misleading about the collection — a scope decision rather than an oversight,
since the API exposes no author filter.

**Piece 2 — a detail pane, and a bug piece 1's own pattern would have
prevented.** [`quote-detail.ts`](../../quotes-ui/src/app/quote-detail.ts) reads
`GET /api/quotes/{id}` for whichever row is clicked, from the brief in
[`BRIEF-DETAIL.md`](../../quotes-ui/BRIEF-DETAIL.md). The first pass fetched it with
a plain `HttpClient.get().subscribe()` instead of a second `httpResource`,
which produced two real defects: a `catchError` that mapped every failure —
including a real 404 for a quote deleted after the list loaded — to the same
generic "not found," and no cancellation between one selection and the next,
so selecting quote 1 then quickly quote 2 could show quote 1's late response
overwriting quote 2 on screen. Neither was caught by clicking — there is no
live Week-1 API in this environment — but both were caught by
[`quotes-api.detail.spec.ts`](../../quotes-ui/src/app/quotes-api.detail.spec.ts),
which controls the exact order two mocked responses arrive in. Fixed by
replacing the subscription with `httpResource`, i.e. piece 1's own
cancel-the-in-flight-request reasoning, applied to the piece that had skipped
it. Full account, including the failing output from the draft and the passing
output after the fix, in [`VERIFICATION.md`](../../quotes-ui/VERIFICATION.md).

---
