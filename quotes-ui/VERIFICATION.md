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
