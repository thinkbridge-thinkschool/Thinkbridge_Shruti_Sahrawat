# The brief — Day 13, piece 2

The prompt given to the agent, before this component existed. Piece 1 was the
list screen in [`BRIEF.md`](BRIEF.md); this is what came after it.

---

Add a detail view to the existing quotes-ui app. Clicking a quote in the list
shows its full detail below the list: no routing, no new page, just a second
component reading a second endpoint.

## The API

`GET /api/quotes/{id}` — already running as part of the Week 1 API. On a real
id it returns 200 with the same shape `GET /api/quotes` already returns per
item:

```json
{ "id": 1, "author": "Ada Lovelace", "text": "…", "createdAt": "2026-03-14T09:30:00" }
```

Reuse the `Quote` interface in `quotes.ts` — do not invent a second type for
the same shape. On an id that does not exist it returns 404 with a
`ProblemDetails` body (`title`, `status`, `detail`). That is a real case here,
not a hypothetical one: `DELETE /api/quotes/{id}` exists in this API, so a row
visible in a stale list can 404 by the time it is clicked.

## What to build

- `QuoteDetail`, a new standalone component, `inject()`-based, `OnPush`,
  rendered alongside `QuotesList` — not inside it.
- Wire it to the list: clicking a row selects that quote's id and the detail
  pane loads it. Clicking the same row again deselects it.
- Signals for the fetch's loading / error / data, typed against `Quote`. No
  `any` anywhere in the new code.
- Four states in the template, `@switch`, no nested `@if` pyramid: nothing
  selected yet, loading, error (with the HTTP status when there is one — same
  distinction `QuotesList` already draws between "the API answered and
  failed" and "nothing answered at all"), and ready.

## Constraints

Same as piece 1: standalone, `inject()`, zoneless, every piece of state a
signal, `computed()` for anything derived. `OnPush` on the new component too.

## Verify it before standing behind it

Exercise loading, error, and the empty/nothing-selected state. Also exercise
what happens when the list and the detail fetch can interleave — click one
row, then quickly click a different one before the first response arrives.
Nothing about `httpResource`'s auto-cancellation is assumed to carry over
just because piece 1 used it; check whether this piece actually uses the same
mechanism, or something that only looks like it does.

Read the diff like a colleague's PR, not like a rubber stamp. Catch at least
one thing that is wrong — a guessed field name, a swallowed error, an `any`
that slipped in somewhere — and have it fixed rather than noting it and
moving on.

---

## What I changed after reading the output

Written up in full in [`VERIFICATION.md`](VERIFICATION.md), under "Piece 2".
Short version: the first pass fetched the detail with a plain
`HttpClient.get().subscribe()` instead of `httpResource`, guarded by a
`catchError` that collapsed *every* failure — a real 404 included — into the
same generic "not found," and had no cancellation at all between one
selection and the next. Both were real, both were caught by a test that
mocked the two requests and controlled the order their responses arrived in,
not by reading the code and guessing. Both are fixed in the same commit as the
test that caught them, by replacing the subscription with a second
`httpResource`.
