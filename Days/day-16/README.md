[← Back to full README](../../README.md)

## Day 16 — Routing, lazy loading, guards

[`quotes-ui/BRIEF-ROUTING.md`](../../quotes-ui/BRIEF-ROUTING.md) asked for four
things against the real Week-1 API this app already talks to: lazy-loaded
routes, route params read from the real detail endpoint, a functional auth
guard, and a View Transition between the quotes list and a quote detail.

**Three real routes replace the always-visible layout.** `app.ts` used to
compose `QuotesList`, `QuoteForm` and `QuoteDetail` all at once inside a
two-column `.shell` grid. `app.routes.ts` now routes `quotes` (the list),
`quotes/new` (the create form, guarded), and `quotes/:id` (the detail page)
to their own pages, each via `loadComponent` rather than a top-level import —
proven separate in `npm run build`'s own chunk output, not just asserted:
four named lazy chunks (`quotes-list`, `quote-form`, `quote-detail`,
`login-page`), none of them in the initial bundle.

**Route params, and the boundary Angular's router doesn't draw.**
`QuoteDetail`'s `id` input is bound straight from the route by
`withComponentInputBinding()` — no `ActivatedRoute` injected in the
component at all. But `GET /api/quotes/{id}` in
[`EndpointExtensions.cs`](../../QuotesApi/Extensions/EndpointExtensions.cs) is
declared `MapGet("/{id:int}", ...)` — a route constraint Angular's router has
no equivalent of. `quotes/:id` matches *any* string in that segment, valid
or not, and hands it straight to the component.

**A functional auth guard, and a real place to send its redirect.**
`authGuard` (`CanActivateFn`) protects `quotes/new` against
`AuthTokenStore.token()` — the same stub Day 15 built, since there's still
no real login flow and no server-side auth today. It returns a `UrlTree`
redirect to a new `LoginPage`, not `false`: a guard that returns `false`
alone cancels the navigation and stops there, with no atomic way to also
land somewhere useful. `LoginPage` sets a demo token and sends the visitor
on to wherever they were originally headed, via a `redirectTo` query param —
made this way on purpose, so the guard's redirect is something a reviewer
can actually click through, not a route that quietly 404s.

**A View Transition via the router's own support**, `withViewTransitions()`
in `app.config.ts`, rather than hand-wiring `document.startViewTransition()`
into click handlers — every navigation gets it, not just the one pair of
routes it was asked for.

**One bug, a route `:id` nothing validated.** The draft read `:id` with a
bare `Number(this.id())` straight into the fetch URL. `/quotes/abc` built
and sent `GET /api/quotes/NaN` — and because the server's `{id:int}`
constraint means that request never reaches `GetById`'s own handler at all,
it comes back as ASP.NET's generic routing 404 instead of the friendly,
typed one this app already knows how to render. `parseQuoteId`
(`quote-id.ts`) draws that line client-side instead: an anchored `^\d+$`
check before a request is ever built, with a dedicated `'invalid'` state for
whatever fails it.

Full write-up, including the lazy-chunk build output, the states/edges
table, and what breaks if the id contract or the route structure changes,
in [`VERIFICATION-ROUTING.md`](../../quotes-ui/VERIFICATION-ROUTING.md).

## Day 16, task 2 — State management, signals first

[`quotes-ui/BRIEF-STATE.md`](../../quotes-ui/BRIEF-STATE.md) asked for a small
feature's state modelled as a signal store against the real Week-1 API —
signals first, no store library — plus the rule for when this app *should*
reach for one.

**One store replaces a split that was accidental.** `page` and `size` lived
in `QuotesApi`; `authorFilter`, `totalCount`, `totalPages`, `visibleQuotes`
and the state machine lived in `QuotesList`. Defensible, but stated nowhere.
[`quotes-store.ts`](../../quotes-ui/src/app/quotes-store.ts) makes the rule the
code's shape: **query state** (`page`, `size`) is in the request URL so
writing it fetches; **view state** (`authorFilter`) never reaches the server;
**server state** belongs to `httpResource` rather than being copied out of
it; **everything else is `computed`**. `QuotesList` is now a thin reader that
derives nothing.

**One new feature, chosen because it forces the interesting states.**
`DELETE /api/quotes/{id:int}` has been implemented since Day 1 and nothing
in the UI ever called it. It answers `204 No Content` — no body — or a plain
`ProblemDetails` 404. The delete is applied optimistically: the row leaves on
click, not on the server's answer, and comes back if the request fails. A
`404` is deliberately treated as success rather than rolled back — the server
is reporting the quote is gone, which is what the user asked for, and
restoring a row the next refetch would remove again is a flicker that tells
the user something untrue.

**One bug, and it was on the success path.** The draft held the
optimistic-removal set as a plain signal that only ever grew. That is correct
while the server is still returning the removed row — but once the
post-delete refetch landed, the server's own `totalCount` had already
dropped, and the store subtracted the row a second time. The server said one
quote remained; the pager rendered "0 quotes total" with a row visibly on
screen. Every delete permanently cost the count one more. The fix makes the
mask a `linkedSignal` keyed on the resource payload, so an id is masked only
while the server still returns it — the mask prunes itself, and no code path
has to remember to clean up. Rollback then reduces to lifting the mask for
the one failed id, which also makes it structurally impossible for one
delete's failure to resurrect a different, overlapping delete that succeeded.

**The judgment call.** [`WHEN-TO-ADOPT-NGRX.md`](../../quotes-ui/WHEN-TO-ADOPT-NGRX.md)
is the threshold for reaching for a store library, written as five tripwires
that can each be checked against the repo and answered yes or no — three or
more independent features *writing* the same state, "how did we get here"
becoming a recurring question, one action fanning out to three or more
unrelated features, optimistic updates needing to queue or undo as a set, or
more than two or three people writing state code in the same area. It also
separates `@ngrx/signals` from full NgRx Store + Effects, because those are
not the same commitment and should not share a threshold.

Full write-up, including the states/edges table and what breaks if the
delete or list contract changes, in
[`VERIFICATION-STATE.md`](../../quotes-ui/VERIFICATION-STATE.md).
