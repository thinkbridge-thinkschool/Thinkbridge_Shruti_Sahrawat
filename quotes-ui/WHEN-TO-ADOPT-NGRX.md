# When this app should reach for a store library

Day 16. Drafted by the agent, at my instruction, then edited and adopted as
mine — so it is phrased as tripwires I can actually check the repo against
and answer yes or no, not as a general essay about when NgRx is good.

## First: "NgRx" is two different decisions now

Worth separating, because they have very different thresholds:

- **`@ngrx/signals` (SignalStore)** — signal-based state with a defined
  shape: `withState`, `withComputed`, `withMethods`, `patchState`. No
  actions, no reducers, no dispatch. It is roughly what
  [`quotes-store.ts`](src/app/quotes-store.ts) already is by hand, with
  conventions and entity helpers supplied.
- **NgRx Store + Effects (the Redux-shaped one)** — actions, reducers,
  selectors, an effects layer, and a devtools timeline. A genuinely
  different architecture, not a tidier service.

They are not the same commitment and should not share a threshold. Almost
everything below is about the second one.

## Where this app is today

One store, `QuotesStore`: four writable signals (`page`, `size`,
`authorFilter`, and the `removedIds` mask), one `httpResource`, and about
ten `computed` derivations over them. Two components read it — `QuotesList`
and `QuoteForm`. `QuoteDetail` deliberately does **not**: its state is one
route param and one resource, and routing it through a shared store would
add a dependency for nothing.

That is small enough that a store library would be pure ceremony. The
honest reason to stay on signals + a service is not "NgRx is bloat" — it is
that nothing here is currently hard.

## The tripwires

Adopt a store library when **any one** of these is true. Each is written to
be checkable against the repo.

**1. Three or more features that don't own each other both read and write
the same state.** Reading is cheap — any number of components can read a
signal without coupling. Writing is what hurts: once three independent
features can mutate the same value, "who set this, and when" stops being
answerable by reading one file. *Today: one writer per piece of state.
`QuoteForm` writes nothing; it calls `createQuote` and the store owns the
consequence.*

**2. "How did we get into this state?" becomes a question I ask more than
once a week.** Signals give you the current value with no history. When a
bug report is "the count was wrong and I don't know why", a devtools
timeline of dispatched actions is worth the whole Redux tax, and nothing
about signals substitutes for it. *Today: state transitions are few enough
that the `effect()` log in `QuotesList` covers it.*

**3. One user action has to trigger reactions in three or more unrelated
features.** Signals handle this with `effect()`, but each one is registered
wherever someone happened to put it, and there is no single place to see
what a given action sets off. That is precisely the problem NgRx Effects
exists to solve. *Today: `deleteQuote` triggers exactly one thing — a
refetch of the list it already owns.*

**4. Optimistic updates need to queue, retry or be undone as a set.**
[`deleteQuote`](src/app/quotes-store.ts) is one-at-a-time and self-healing —
its mask prunes against the server's own answer, which is why it survives
two overlapping deletes without a coordination layer. The moment we need an
undo stack, an offline queue, or "these four edits commit together", that
self-healing property stops holding and hand-rolling the replacement is how
you get the bug this exercise already caught, but harder to find.

**5. More than two or three people are writing state code in the same area
at once.** A convention everyone can follow beats minimal code that only its
author can navigate. This one is about the team, not the app, and it is the
only tripwire that can fire without the code changing at all. *Today: one
person.*

## What would NOT trip it

Stated deliberately, because these are the reasons people usually reach for
NgRx and none of them are good enough on their own:

- **More components reading the store.** Reading is not coupling.
- **More derived values.** `computed` scales fine; ten is not meaningfully
  worse than three.
- **More endpoints.** Another `httpResource` is another field, not another
  architecture.
- **"The app is getting big."** Size is not the variable — *shared mutable
  state* and *traceability* are. A 40-component app where each feature owns
  its own state needs a store library less than a 6-component one where
  everything writes to everything.

## The middle step I would take first

If tripwire 1 or 5 fires but 2 and 3 do not, the answer is
**`@ngrx/signals`**, not full NgRx: it gives the shape and the entity
helpers without introducing actions and an effects layer to reason about.
The migration is also genuinely small from where this code already sits —
`withState` for the four writable signals, `withComputed` for the
derivations, `withMethods` for the commands. That is the honest reason to
have written the store this way by hand first: it makes the next step a
refactor rather than a rewrite, and it means we would be adopting the
library for a reason we can name rather than by default.
