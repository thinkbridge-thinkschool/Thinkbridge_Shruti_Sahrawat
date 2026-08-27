# The brief — routing, lazy loading, guards

The prompt given to the agent, before any route existed. Day 16.

---

Real client-side routing, replacing the always-visible list+form+detail
layout in `app.ts` with actual navigable pages. Four things, all against the
real Week-1 API this app already talks to:

**Lazy-loaded routes.** Three feature routes — the list, the create form, the
detail page — each its own chunk via `loadComponent`, not a top-level import.
I want to see this proven against `npm run build`'s own output, not asserted.

**Route params.** The detail page reads its quote id from the URL
(`quotes/:id`), not from a click handler on a sibling component the way it
does today. Read the real endpoint before wiring this up: `GET
/api/quotes/{id}` in `QuotesApi/Extensions/EndpointExtensions.cs` — note
exactly how that route is declared, not just that it exists. Angular's router
has no equivalent of whatever constraint you find there, so decide what
happens when the URL segment doesn't satisfy it, and don't leave that to the
server to reject.

**A functional auth guard.** Something protects a route and redirects an
unauthenticated visitor. There's no login flow and no server-side auth in
this app yet (Day 15 already established that) — decide what a guard means
in that situation, and where it should send someone it turns away, and make
that redirect something a reviewer can actually click through, not a dead
end.

**A View Transition between the quotes list and a quote detail.** Use the
Angular Router's own support for this rather than hand-rolling
`document.startViewTransition()` calls in click handlers.

## Verify before handing back

State/edge coverage I expect to see exercised, not just described: the guard
passing with a token set versus redirecting without one, the lazy chunk
actually showing up separately in a build, and a route param that doesn't
look like a valid id. Tell me specifically what you checked and how — "it
looks like it works" isn't a verification method.

## Reasoning goes in comments

Why the guard returns what it returns instead of `false`. Why
`withComponentInputBinding()` over injecting `ActivatedRoute` by hand. What
happens to the old row-selection state now that list and detail are separate
pages. I will be asked to defend each one.

---

## What I changed after reading the output

Written up in [`VERIFICATION-ROUTING.md`](VERIFICATION-ROUTING.md). The
draft read the route's `:id` with a bare `Number(this.id())` and used it to
build the fetch URL with no validation at all — `/quotes/abc` built and sent
`GET /api/quotes/NaN`. I made the agent fix it: reject anything that isn't a
plain positive integer before a request is ever built from it, with a state
the page renders for exactly that case.
