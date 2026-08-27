# The brief — state management, signals first

The prompt given to the agent, before `QuotesStore` existed. Day 16.

---

Model the quotes-list feature's state as a signal-based store service against
the real Week-1 API. Signals first — no NgRx, no store library. Then tell me,
in writing, the threshold at which you *would* reach for one.

## The feature

The list screen today splits its state across two files with no clear rule:
`QuotesApi` owns `page` and `size`; `QuotesList` owns `authorFilter`,
`totalCount`, `totalPages`, `visibleQuotes` and the `state` machine. That
split happens to be defensible — query state versus view state — but it is
not *stated* anywhere, and the component is now doing enough derivation that
it is hard to tell which signals are the source of truth and which are
consequences.

Consolidate it into one `QuotesStore` and make that distinction explicit in
the shape of the code, not just in a comment. `QuotesList` should end up a
thin reader: template plus intents, no derivation of its own.

## Add one thing that doesn't exist yet: delete

`DELETE /api/quotes/{id:int}` is implemented server-side and nothing in the UI
calls it. Read it before you wire it up —
`QuotesApi/Extensions/EndpointExtensions.cs`:

```csharp
group.MapDelete("/{id:int}", async (int id, IQuoteRepository repo, CancellationToken ct) =>
{
    var deleted = await repo.DeleteAsync(id, ct);
    return deleted
        ? Results.NoContent()
        : Results.NotFound(new ProblemDetails { Title = "Quote not found", Status = 404, Detail = $"No quote with id {id}." });
});
```

**`204 No Content` on success — no body at all.** Not `200`, not the deleted
quote echoed back. And a `404` carrying a plain `ProblemDetails`, the same
shape `GET /{id}` returns for a missing quote (Day 15 pinned this in
`api-contract.spec.ts` — the 404 has no `errors` dictionary, unlike the
`POST`'s 400).

**Apply the delete optimistically**: the row leaves the list when the user
clicks, not when the server answers. If the request fails, the row comes
back and the user is told. This is the part I actually want to see modelled
carefully, because it is where this feature stops being a read-only screen.

Three things to decide and defend:

1. **What `totalCount` should read while a delete is in flight.** The server
   said 42; you have optimistically removed one. The pager is derived from
   that number.
2. **What a `404` on delete means.** The server is telling you the quote is
   not there. Is that a failure to roll back, or the outcome the user
   wanted, arriving by a different route than expected?
3. **What happens when two deletes overlap.** The user deletes two rows in
   quick succession and one of them fails. Be explicit about what your
   rollback restores — I want to see that you have actually thought about
   the second response arriving after the first, not just the single-delete
   happy path.

## States I expect exercised

Loading, error, empty, and the two empty cases kept apart the way the list
already keeps them apart (`no-data` — the API returned nothing — versus
`no-matches` — the filter excluded everything). Plus the delete states:
in-flight, succeeded, failed-and-rolled-back, and the concurrent case above.

The author filter stays client-side over the current page, as today — a
keystroke must not cause a fetch. If your store makes the filter part of the
request, you have changed the feature, not refactored it.

## The judgment call

Separately from the code: write me the rule for when this app should adopt
NgRx (or any store library) instead of signals + a service. Not a general
essay about when NgRx is good — the specific threshold *this codebase* would
have to cross, phrased so that a reviewer could hold the code up against it
and get a yes or a no. I will be defending this rule as mine, so it has to
be something I can actually stand behind, which means concrete conditions
rather than "when the app gets complex."

## Reasoning goes in comments

Why each piece of state is a source of truth or a derivation. Why the
optimistic update is modelled the way it is. What the rollback restores and
what it deliberately does not touch. I will be asked to defend each line.

---

## What I changed after reading the output

Written up in [`VERIFICATION-STATE.md`](VERIFICATION-STATE.md). The draft
rolled back a failed delete by restoring a whole-list snapshot taken when
that delete started — so with two deletes in flight, the second one's
rollback resurrected the first one's row, which the server had already
deleted. I made the agent replace the imperative snapshot-and-restore with
derived state, so a rollback touches exactly the row that failed.
