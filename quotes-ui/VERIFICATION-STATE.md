# Verification log — state management, signals first

Day 16, task 2. What was exercised, the one bug that came back wrong, and
what breaks if the Week-1 API contract changes.

## How this was verified

`HttpTestingController`, not a live Week-1 API — there is no running
instance in the environment this was built in. What that does and does not
prove: it proves the store reacts correctly to the exact response shapes
this API sends (`204 No Content` with no body on delete, a plain
`ProblemDetails` 404, the `items`/`page`/`size`/`totalCount` envelope on the
list), each transcribed from
[`EndpointExtensions.cs`](../QuotesApi/Extensions/EndpointExtensions.cs)
rather than invented. It does not prove behaviour against real network
timing — the "two deletes overlap" case below is two mocked responses
flushed in a chosen order, not a real race.

`errorMappingInterceptor` is wired into the spec's `TestBed`, deliberately.
That was not the first version: the initial run failed the 404 case because
without the interceptor the thrown value is a raw `HttpErrorResponse` whose
`kind` is `undefined`, so `deleteQuote`'s `notFound` branch never matched
and every failure looked alike. Testing the store without the interceptor
its production `app.config.ts` always runs alongside would have been testing
a shape the real app never produces.

## States and edges exercised

| State / edge | How it was forced | Result |
|---|---|---|
| Loading | assert before flushing the initial list | `listState()` is `'loading'` |
| Ready | flush the real `PagedResult` envelope | `'ready'`, rows in server order, `totalCount` from the envelope |
| Empty — API returned nothing | flush `{items: [], totalCount: 0}` | `'no-data'` |
| Empty — filter excluded everything | two rows, `setAuthorFilter('Grace')` | `'no-matches'`, kept distinct from `'no-data'` — different words, different recovery action |
| Error | flush 500 on the list | `'error'` |
| Filter is client-side | `setAuthorFilter('ada')` over a loaded page | narrows to 1 row; `httpMock.verify()` is the assertion — a keystroke that reached the server would leave an unflushed request behind |
| Delete, optimistic | `deleteQuote(1)`, assert **before** flushing the DELETE | row gone from `visibleQuotes()` while the request is still in flight |
| Delete, count while in flight | server said 42, one row removed | `totalCount()` reads 41 — the count and the rows on screen agree |
| Delete, confirmed | flush `204`, then the refetch | row stays gone, count settles to the server's own number, `deleteError()` null |
| Delete, failed | flush `500` | row returns in its original position, count returns, `deleteError()` set |
| Delete, `404` | flush the plain `ProblemDetails` 404 | row stays gone, **no** rollback, no error shown — see the decision below |
| Two deletes overlap, one fails | delete 1 and 2; flush `204` for 1, then `500` for 2 | only quote 2 returns; quote 1 stays gone |

**70 tests total, all green:** the 58 carried forward from Days 13–16 task 1
unchanged in substance, plus 12 for `QuotesStore`.

## The decision the brief asked for: what a 404 on delete means

`DELETE /api/quotes/{id:int}` answers `404` with a plain `ProblemDetails`
when the row is not there. The draft and the fix both treat that as
**success, not as something to roll back** — and this is a decision worth
defending rather than an oversight.

The user asked for the quote not to exist. The server is reporting that it
does not exist. Rolling back would restore a row that is genuinely gone
server-side — and the refetch that follows would remove it again a moment
later, so the visible result is a flicker that tells the user something
untrue. The two realistic causes are both benign: someone else deleted it,
or this is a retry of a request that already succeeded.

The case where this is wrong: if a future `{id}` route started returning
`404` for *authorisation* failures — "this exists but is not yours" — then
silently treating it as deleted would hide a real refusal. Today it cannot:
`Program.cs` registers no authentication at all, and the only `404` the
endpoint constructs is the genuine not-found one.

## The bug: an optimistic mask that only ever grew

`quotes-store.spec.ts` was written against the brief before the store
existed. Against the draft, unchanged: **1 failure.**

```
FAIL  quotes-store.spec.ts > deleteQuote > keeps the row gone once the server confirms with 204
AssertionError: expected +0 to be 1

- Expected   1
+ Received   0

  196|       expect(store.visibleQuotes().map((q) => q.id)).toEqual([2]);
  197|       expect(store.totalCount()).toBe(1);
     |                                  ^
```

The draft held `removedIds` as a plain `signal<Set<number>>` that grew on
every delete, plus a whole-list `rollbackSnapshot` to restore on failure.
`totalCount` was `serverTotal - removedIds.size`.

That is correct for exactly as long as the server is still returning the
removed row. The moment the post-delete refetch lands, the server's own
`totalCount` has *already* dropped — but the id was still sitting in
`removedIds`, so the store subtracted it a second time. The server said one
quote remained, the mask said "minus one more", and the pager rendered
**"0 quotes total" with a row visibly on screen**.

**Why it is easy to miss.** It is on the *success* path. Every failure case
worked; the optimistic removal worked; the row was correctly gone. Clicking
delete and glancing at the list looks completely right — the wrong number
is in the pager, one line below where you are looking, and only after the
refetch resolves. And it needs real data to show up at all: with
`totalCount` of 1 going to 0 it reads as an off-by-one, but the shape of the
bug is "every delete permanently costs the count one extra", so a page with
several deletions drifts further and further from the truth.

**Root cause, and why the fix is structural rather than a patch.**
`removedIds` was a parallel copy of server state that something had to
remember to reconcile — and nothing did. The fix makes it a `linkedSignal`
keyed on the resource payload:

```ts
private readonly removedIds = linkedSignal<PagedResult<Quote> | undefined, ReadonlySet<number>>({
  source: () => this.resource.value(),
  computation: (payload, previous) => {
    const prev = previous?.value ?? EMPTY_IDS;
    if (payload === undefined) return prev;   // mid-fetch: keep masking
    if (prev.size === 0) return prev;
    const stillReturned = new Set(payload.items.map((q) => q.id));
    return new Set([...prev].filter((id) => stillReturned.has(id)));
  },
});
```

The rule it encodes: **an id is worth masking only while the server is still
returning it.** Once the server stops, the mask is not merely redundant, it
is actively wrong. Deriving that from the payload means no code path has to
remember to clean up, which is why the imperative version had a bug and this
one structurally cannot.

The `payload === undefined` guard is load-bearing and is the same lesson
Day 13 learned for `totalCount`: `httpResource` clears `value()` to
`undefined` whenever the request parameters change, so pruning against "no
items" would drop every mask and flash the deleted rows back for a frame
before the new page arrived.

**The rollback got simpler for free.** The draft restored a whole-list
snapshot, which cannot distinguish "this delete failed" from "a different,
overlapping delete succeeded" — with two in flight and one failing, the
snapshot would have resurrected a row the server had already deleted. The
fix lifts the mask for exactly the failed id and touches nothing else, so
that class of bug is not fixed so much as made unrepresentable. The
overlapping-deletes test in the table above guards it either way.

## What breaks if the Week-1 API contract changes

**`DELETE` starts returning `200` with the deleted quote instead of `204`.**
Nothing breaks immediately — `firstValueFrom` on a `delete<void>` resolves
either way, and the store ignores the body. But it would be a signal that
the endpoint now has something to say, and the store would be throwing it
away: the natural next step would be to use the returned entity rather than
refetching the whole page, and nothing here would prompt that.

**`GET /api/quotes` stops returning `totalCount`,** or renames it (to
`total`, say). `serverTotal` falls back to its `linkedSignal` previous value
— which means the pager would silently freeze at whatever it last read
rather than erroring. Quiet, and the kind of thing only
`api-contract.spec.ts` would catch, which is exactly why that
characterization test exists as a separate file from the code that consumes
the contract.

**Deletion becomes soft-delete** — the row keeps being returned by
`GET /api/quotes` with a `deletedAt` field. The mask would prune correctly
(the id *is* still returned), so the row would reappear on the next refetch
and the delete would look like it silently failed. This is the change most
likely to break this design, and the fix would be filtering on the server's
own flag rather than masking client-side — the mask exists precisely because
the server currently has no way to express "pending deletion".

**Pagination becomes cursor-based** rather than page/size. `page` and `size`
are the two signals the request URL is built from, so this is a change to
the store's query-state shape and everything derived from it — `totalPages`,
the pager's disabled states, `firstPage()`. Contained to this file, which is
the argument for the store owning query state in the first place, but not
small.
