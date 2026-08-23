# The brief

The prompt given to the agent, before any code existed.

---

Build a single-screen Angular 21 app that lists quotes from the API I wrote in
Week 1. One screen only — no routing, no UI library, no auth, no extra
components beyond what this needs.

## The API

It runs locally on `http://localhost:5067`. Proxy `/api` through the dev server
rather than calling it cross-origin: the API registers no CORS policy and I am
not changing the API for a front-end exercise.

```
GET /api/quotes?page=1&size=10   →  200 application/json

{
  "items": [
    { "id": 1, "author": "Ada Lovelace", "text": "…", "createdAt": "2026-03-14T09:30:00" }
  ],
  "page": 1,
  "size": 10,
  "totalCount": 10000
}
```

Use those names exactly. It is `totalCount`, not `total`. `createdAt` is an
ISO-8601 string with no offset — type it as `string`, not `Date`, because
nothing revives it and calling `.getTime()` on it would fail at runtime while
compiling fine.

The server clamps its own inputs: `page <= 0` becomes 1, `size <= 0` becomes 10,
and `size` is capped at 100. Mirror those bounds in the UI so it never sends a
request it knows will be rewritten — but the server stays the authority, and I
want a note in the code saying so.

## Data fetching

Use `httpResource`, not a hand-rolled `HttpClient` subscription. I want
`value()`, `isLoading()`, `error()` and the HTTP status as signals, and I want
the request re-issued automatically when the page or size signal changes, with
the in-flight request cancelled.

Put it behind an `@Injectable` service and pull that service into the component
with `inject()`, not constructor injection. Split the state by whether it
changes the request: page and size belong to the service because they change the
URL; the author filter belongs to the component because it narrows rows already
fetched and must never trigger an HTTP call.

## State

Three writable signals: `page`, `size`, and an author filter. At least one
`computed()` derived from **two** of them. Use `effect()` for something
read-only — a console log of state transitions I can screenshot as verification.
No effect that writes back to a signal it reads.

## Template

New control flow only.

- `@switch` over a single computed state. Five distinct states, not a pile of
  nested `@if`s: loading, error, no data from the API, no matches for the
  filter, ready. Treat "the API returned nothing" and "your filter matched
  nothing" as different — they need different wording and different recovery
  buttons.
- `@for` with `track`. Track the quote `id`, not `$index`, and put the reason in
  a comment.
- `@if` where a condition is genuinely binary.

## Constraints

- Standalone components. No `NgModule` anywhere.
- `inject()` for every dependency.
- Zoneless. Angular 21 is zoneless by default, so do **not** add
  `provideZonelessChangeDetection()` — it is not a provider you need any more.
  Every piece of state must be a signal, because nothing patches `setTimeout` or
  `addEventListener` to tell Angular to re-check.

## Explain your reasoning in comments, not prose

Why `track q.id` and not `$index`. Why `createdAt` is typed the way it is. What
an absent HTTP status means as distinct from a 500. I will be asked to defend
these, so I want the reasoning next to the code rather than in a document that
drifts away from it.

---

## What I changed after reading the output

The brief above is what I sent. Three things came back wrong, and I made the
agent fix each one — they are written up in [`VERIFICATION.md`](VERIFICATION.md):
a wrong API member that TypeScript caught, a `?? 0` default that invented a fact
whenever the response was late, and a missing state — a request that never
settles, which rendered as "loading" forever because I had only asked for
loading, error and ready.

That last one was a gap in this brief, not only in the code. **"Never answered"
is a fourth state**, and I did not think to ask for it.
