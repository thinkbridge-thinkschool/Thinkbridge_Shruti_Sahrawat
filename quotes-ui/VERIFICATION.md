# Verification log

## What I exercised

Run against the Week-1 API on `http://localhost:5067`, proxied through the
Angular dev server so the browser stays on one origin. The database held **10,000
quotes** left over from the Day 11 performance work, which made paging and the
filter's limits real rather than theoretical.

| State / edge | How I forced it | What I saw |
|---|---|---|
| `loading` | first paint | Rendered, then replaced by the list |
| `ready` | default page, size 10 and size 100 | List renders, pager reads "Page 1 of 1000" at size 10 |
| `no-matches` | filter `Ada` on page 2 | "100 quotes on this page, none by an author matching Ada", with a Clear filter button — **not** the `no-data` wording |
| `error` (API stopped) | Ctrl+C the API, click Next | Hung on `loading` indefinitely. See finding three — fixed with a timeout interceptor |
| computed reacts to a signal | page size 10 → 100 | `totalPages` recomputed 1000 → 100 and the pager updated with it, no manual refresh |
| size clamping | typed `500` into page size | Input showed `100`; `totalPages` computed from 100, matching the server's own cap |
| pager during refetch | Next with the API down | "Page 3 of 1 (0 quotes total)" — finding two |
| `no-data` | not exercised | — |
| `track` correctness | not exercised | — |

The two states left blank are the honest gaps. `no-data` needs a page past 100
at size 100; `track` needs watching rows across a page change closely enough to
catch stale text.

## Three things the agent got wrong

All three were found by compiling or running, not by being told. They are in the
order they surfaced.

**One — caught by the compiler.** The agent wrote
`@if (quotes.status() === 0)` to detect an unreachable API. That does not
compile: on `HttpResourceRef`, `status()` is the *resource lifecycle*
(`'idle' | 'loading' | 'resolved' | 'error' | …`), while the HTTP status code is
`statusCode()`, and it is `undefined` — not `0` — when the request never reached
a server. Two wrong assumptions in one line, caught by `tsc` before it could
reach a browser. Worth noting *why* it slipped through: the member name reads
like the thing you want, and a wrong guess about an API you half-remember
compiles fine right up until the types disagree.

**Two — caught by running it with the API down.** With the API stopped and Next clicked, the pager
read **"Page 3 of 1 (0 quotes total)"**.

`totalCount` was `computed(() => this.quotes.value()?.totalCount ?? 0)`, and
`httpResource` clears `value()` to `undefined` whenever the request parameters
change — not only on failure. So on *every* page change, for as long as the
fetch was in flight, the count collapsed to 0 and `totalPages` collapsed to 1.
Normally that is a flicker fast enough to dismiss as a rendering artefact. With
the API down it froze, which is the only reason it got noticed.

Fixed with `linkedSignal`, which is built for a value derived from a source that
may go momentarily absent: it carries the previous value forward instead of
falling back to a zero that is not true.

The general shape is worth keeping: **a `?? 0` fallback on data that is merely
*late* rather than *absent* invents a fact.** The bug was in the default, not in
the fetching.

**Three — caught by unplugging the API, and the most interesting of the three.** With the API stopped, the screen sat on
"Loading quotes…" indefinitely — it never reached the error state, and was still
there after the API had been restarted.

The request had not silently succeeded, and it had not slowly failed. It failed
*immediately*: the dev-server proxy logged
`http proxy error: /api/quotes?page=1&size=10  AggregateError [ECONNREFUSED]`
at the moment of the click. Having already begun handling the request, it then
never sent a response. So the fetch never settled, `httpResource` never left
`loading`, and the error branch — the one whose `statusCode()` bug is finding
one above — never ran at all. The failure was real, instant, and invisible.

That specific cause is a dev-server artifact and would not occur behind a
production gateway. The *class* of failure is not: a proxy that dies
mid-request, a load balancer that drops a connection, and a server that accepts
and then never answers are indistinguishable from the browser — a promise that
stays pending forever. Nothing in `HttpClient` bounds that by default.

Fixed with a 10-second timeout interceptor (`request-timeout.ts`), which turns a
hanging request into an error the UI can render. Written as an interceptor
rather than in the component, so it cannot be forgotten at one call site.

Worth noting what this says about the three states I asked for. `loading`,
`error` and `ready` are not exhaustive: **"never answered" is a fourth**, and it
renders as `loading` unless something forces it to become an error.

---

## Piece 2 — quote detail (`GET /api/quotes/{id}`)

### What I exercised

There is no live Week-1 API in the environment this piece was built in, so
"running it" meant something different here than for piece 1: a real
`GET /api/quotes/{id}` against a mocked HTTP backend, driven by
[`quotes-api.detail.spec.ts`](src/app/quotes-api.detail.spec.ts) and
`HttpTestingController`, rather than clicking through a browser against the
real dotnet process. What that buys over reading the diff: control over
exactly *when* each mocked response resolves, which is what the race below
needs and clicking cannot reliably force.

| State / edge | How I forced it | What I saw |
|---|---|---|
| idle (nothing selected) | fresh service, no selection | `{ status: 'idle' }` |
| loading | select a quote, check state before flushing | `{ status: 'loading' }` |
| ready | select, flush a 200 with a real `Quote` body | `{ status: 'ready', quote }` |
| cleared back to idle | select, load, then select `null` | `{ status: 'idle' }` |
| error, with the status code preserved | select an id, flush a 404 `ProblemDetails` body | draft: `{ status: 'error' }` — no code. Fixed: `{ status: 'error', statusCode: 404 }` |
| stale-response race | select 1, then select 2 before 1 resolves, flush 2 then 1 | draft: shows quote **1** — wrong. Fixed: shows quote **2** — right |

### Two things the agent got wrong

Both in the same first pass, both caught by the test above — not by reading
the code and guessing, and not by clicking, since there was nothing running to
click against.

**One — a swallowed error.** The draft fetched the detail with
`HttpClient.get(...).pipe(catchError(() => of(null))).subscribe(...)`. Every
failure — a 404, a 500, a dropped connection — collapsed to the same `of(null)`
and came out as `{ status: 'error' }` with no `statusCode`. Run against the
draft, the 404 test failed exactly on that missing field:

```
AssertionError: expected { status: 'error' } to deeply equal { status: 'error', statusCode: 404 }
- Expected
+ Received
  {
    "status": "error",
-   "statusCode": 404,
  }
```

A 404 here is not a hypothetical: `DELETE /api/quotes/{id}` exists in this API
(Day 1, piece 2), so a row visible in a list that has not refetched can be
gone by the time it is clicked. That is a different fact from "the API is
down," and the draft told them apart from nothing.

**Two — a stale-response race, the more interesting one.** The same
subscription had no cancellation. Selecting quote 1 and then, before its
response arrived, selecting quote 2, left both requests in flight with
nothing tracking which one the current selection still cared about. Run
against the draft with quote 2's response flushed first and quote 1's flushed
second — the ordinary case where the first request simply took a slower
path — the final state was quote 1:

```
AssertionError: expected { status: 'ready', …(1) } to deeply equal { status: 'ready', …(1) }
- Expected
+ Received
  {
    "quote": {
-     "author": "Author 2",
+     "author": "Author 1",
-     "id": 2,
+     "id": 1,
-     "text": "Quote text 2",
+     "text": "Quote text 1",
      ...
    },
    "status": "ready",
  }
```

The screen would have shown the *previous* selection, overwriting the one the
user had already moved on to — silently, with no error and no loading
indicator to suggest anything was wrong. This is the same shape of bug piece 1
already had one instance of (`?? 0` inventing a fact about data that was
merely late), but structural rather than a default: there was nothing in the
draft's design that could tell "this response is for a selection I have since
abandoned" from "this response is the one I am waiting for."

**The fix.** Both were fixed together, in the same commit as the test that
caught them, by replacing the subscription with a second `httpResource`:

```ts
private readonly detail = httpResource<Quote>(() => {
  const id = this.selectedId();
  return id === null ? undefined : `/api/quotes/${id}`;
});
```

This is not a bigger fix bolted onto the same design — it is piece 1's own
list resource, and piece 1's own reasoning ("cancelling the in-flight request
first... no 'an older response arrived after a newer one' race to reason
about") applied to the piece that skipped it. `httpResource` aborts the
superseded request itself; `detailState` reads `error()` / `statusCode()`
straight off the resource instead of through a `catchError` that discards the
distinction. Re-run against the fix, all six cases pass, including the two
that failed against the draft:

```
 ✓ src/app/quotes-api.detail.spec.ts (6 tests) 6 passed
```

### Why the same test file works against both

`quotes-api.detail.spec.ts` never mentions `httpResource`, a subscription, or
any other fetching mechanism — it only calls `selectQuote()` and reads
`detailState()`. That façade is what let one test file run unchanged against
the draft (catching both bugs) and then against the fix (confirming both were
gone), rather than needing to be rewritten once the internals changed. The
`respond()` helper checks `request.cancelled` before flushing for the same
reason: the fixed implementation cancels the superseded request outright,
which a test written only against the draft's always-flushable requests would
not have anticipated.

---

## One thing that is arguable rather than wrong

The author filter searches only the page already fetched. With 10,000 quotes and
a page size of 100, typing "Ada" on page 2 returns "none by an author matching
Ada" — and Ada Lovelace is almost certainly somewhere in the other 9,900 rows.
The message is accurate about the page and misleading about the collection.

Honouring the filter properly would mean a server-side query parameter, and the
Week-1 API has none — so this is a scope decision, not an oversight. Left as
page-local, with the wording changed to say "on this page" so the UI does not
claim more than it knows. If this were a real screen the correct fix is on the
API, not in the component.

## What would break if the API contract changed

The uncomfortable common thread: **TypeScript checks none of this.** The
interfaces in `quotes.ts` are erased at build time. Nothing validates the JSON
that actually arrives, so a contract change does not produce an error — it
produces a plausible-looking wrong screen.

**`totalCount` renamed to `total`.** Silent and the worst of the four.
`this.quotes.value()?.totalCount` becomes `undefined`, `linkedSignal` falls
through to its initial 0, `totalPages` floors at 1, and the pager reads "Page 1
of 1 (0 quotes total)" while happily rendering a hundred quotes underneath it.
No error, no console warning, no failed request. Exactly the shape of finding
two, arriving from the other side.

**`createdAt` changing shape or timezone.** `DatePipe` on an unparseable string
throws at render, so this one at least fails loudly. A timezone change is worse
because it does not: the API serialises `DateTime` with no offset, so the
browser reads it as local time. Move the server to UTC storage or add an offset
and every date silently shifts by hours, with nothing to notice.

**The endpoint requiring auth.** Every request returns 401, `httpResource` sets
`error()`, and the UI shows "The API responded with HTTP 401" — correct, and
useless, because there is no login. Handling it properly means an interceptor
attaching a bearer token and a 401 branch distinct from the generic error, which
is a feature rather than a fix.

**The `size` cap moving from 100.** Clamping is duplicated: the server does it,
and `onSize` mirrors it via `MAX_SIZE`. If the server raises its cap to 500 the
UI silently refuses to ask for more than 100 — a limit with no visible cause. If
the server *lowers* it to 50, the UI keeps sending 100, the server quietly
rewrites it, and `totalPages` is computed from a size the server never used, so
the pager over-reports the page count. The second case is worse: the client
believes a number the server disagreed with.

**The seam is the mirror.** Any constant duplicated from the server is a fact
that can go stale without telling anyone. The honest fix is for the API to
report its own limits — return the effective `size` in the response (it already
does) and read that back rather than assuming the request was honoured.

**Piece 2 — the detail endpoint's 404 losing its shape.** `detailState`'s error
branch reads `this.detail.statusCode()` only — it never looks at the response
body. That is deliberate: a `ProblemDetails` shape (`title`/`status`/`detail`)
is exactly the kind of thing a later change swaps for a plain string or a
different casing, and the UI already only promises "the API responded with
HTTP {{ code }}," not the server's wording. The status code is the one part
of a 404 this screen actually depends on, and it comes from the transport
(`HttpErrorResponse.status`), not from parsing the body — so a body-shape
change here is invisible to this component by design, not by luck.

---

## What zoneless changes about change detection

**What Zone.js used to do.** It monkey-patched the browser: `setTimeout`,
`addEventListener`, `Promise`, `XMLHttpRequest`. Any of them firing told Angular
"something asynchronous just finished — something may have changed." Angular
had no idea *what*, so it walked the component tree from the root and re-checked
every binding. Correct, and wasteful in proportion to the size of the app: a
single keystroke re-evaluated bindings in components that could not possibly
have been affected.

**What replaces it.** Nothing is patched, and there is no zone.js in the bundle
at all. A signal knows which computations and which templates read it. When it
changes it marks exactly those dirty, and Angular re-renders only those. The
question moved from "did anything happen anywhere" to "who read this particular
value".

**What that means in this component, concretely.** Typing in the filter box sets
`authorFilter`. That signal is read by `visibleQuotes`, which is read by
`state()` and by the `@for`. Those three re-evaluate. The pager reads `page`,
`totalPages` and `totalCount` — none of which changed — so it is not touched.
Under Zone.js the keystroke would have re-checked every binding on the screen
including the pager.

**What would visibly break if one of these were a plain field.** Replace
`authorFilter = signal('')` with `authorFilter = ''` and assign to it on input:
the value updates in memory, the filter does nothing, and the list never
re-renders. Nothing errors. Nothing warns. Under Zone.js the same code would
have worked by accident, because the `input` event was patched and Angular
re-checked everything anyway. **Zoneless turns "I forgot to make this reactive"
from an invisible inefficiency into a visible bug** — which is the trade being
made.

**Where it bites in practice.** Code that mutates an object in place and expects
the view to notice. `this.items.push(x)` on a plain array read by a template
updates nothing, because the signal holding the array never changed identity.
Every piece of state on this screen is a signal for that reason, and
`visibleQuotes` returns a new array from `filter()` rather than mutating one.

**How this connects to `OnPush`.** Under Zone.js, `OnPush` was the opt-in
optimisation: check this subtree only when an input reference changes or an
event fires inside it. Zoneless makes signal-driven checking the default for
everything, so `OnPush` is close to redundant. It is set explicitly on both
components anyway — as a statement that neither relies on being re-checked by
accident, and so they still behave correctly if dropped into an app that has
opted back into Zone.js.
