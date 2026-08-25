# Verification log — create-a-quote form

Day 14, piece 1. What was exercised, what came back wrong, and what breaks if
the Week-1 quote contract changes.

## How this was verified, and what that does not cover

There is no live Week-1 API in the environment this was built in, and no
screen reader. So the honest description is: **every state below was driven
through `HttpTestingController` and asserted in
[`quote-form.spec.ts`](src/app/quote-form.spec.ts), and accessibility was
checked with axe-core plus direct assertions on the attributes a screen
reader consumes** — not by listening to NVDA read it.

That distinction matters for one claim in particular. Asserting that
`aria-describedby` is on the `<input>` and names an id that exists proves the
wiring is *capable* of being announced. It does not prove any particular
screen reader announces it the way I expect. Testing the DOM contract is the
strongest thing available here; it is not the same as testing the experience,
and if this were shipping I would put it in front of a real screen reader
before saying it works.

axe-core 4.13 runs over the rendered component in jsdom, in both the default
and the error-showing state. `color-contrast` is disabled in that run — jsdom
has no layout, so axe cannot compute it — and the palette is instead checked
against the tokens in `src/styles.css`, which were fixed in the Day 13 polish
pass for exactly this reason.

## States and edges exercised

| State / edge | How it was forced | Result |
|---|---|---|
| pristine | fresh mount | No field errors, no `aria-invalid`, banner present but `hidden` |
| invalid — required | type then clear, blur | `aria-invalid="true"`, message rendered, `aria-describedby` extended |
| invalid — whitespace only | type `"   "`, blur | Invalid. See finding 3 — it was **not**, before the fix |
| invalid — at the limit | 200-character author | Valid, as the API allows. See finding 2 |
| submitting | hold the POST in flight | Submit button disabled, label reads "Saving…" |
| server-error — 400 field errors | flush real `ValidationProblemDetails` | Messages land on the right fields. See finding 1 |
| server-error — 500 | flush 500 | Banner, distinct from field errors, fields left clean |
| success — 201 | flush created quote | Success region names the quote, inputs cleared, list refetched |
| keyboard — focus on failed submit | submit with author empty | Focus lands on the author input. See finding 4 |
| axe — default state | axe-core over the DOM | 0 violations |
| axe — errors showing | both fields invalid, axe again | 0 violations |

The keyboard path itself — tab into each field, type, submit with Enter from
inside the form, reach the "Write another" button — was walked by hand in the
rendered component. What is *asserted* is the part a test can hold: label
association, the ARIA attributes, `novalidate`, and where focus lands.

## Four things the agent got wrong

The spec in `quote-form.spec.ts` was written against the brief, then run
against the first-pass component. **Same file, unchanged: 8 failures against
the draft, 21 passing against the fix.** Seven of those eight are the four
findings below; the eighth is a design difference, described at the end.

**One — server validation errors rendered nowhere.** The draft read
`result.fieldErrors['author']` and `['text']`. The API returns them
capitalised:

```json
{ "errors": { "Author": ["…"], "Text": ["…"] } }
```

Every *other* field this API returns is camelCase — `author`, `text`,
`createdAt` — so camelCase is the natural guess, and it is wrong here for a
specific reason: these keys come from `ValidationResult.MemberNames`, which
are C# property names, into a `Dictionary`, and ASP.NET Core's web JSON
defaults camel-case *property names* but leave *dictionary keys* alone.

This is checked behaviour rather than my reading of the docs.
`Quotes.Tests.Integration/QuoteEndpointsTests.cs:41` asserts
`problem.Errors.Should().ContainKey("Author")` against a real SQL Server via
Testcontainers, and it passes in CI.

The failure mode is the bad kind. The response parses fine, no exception is
thrown, nothing appears in the console — the form simply shows no errors and
looks like it succeeded, while the quote was never created and the list never
changed. The fix matches case-insensitively rather than just swapping to
`Author`: matching only the capitalised form would be correct today and would
break silently the day anyone sets a `DictionaryKeyPolicy` on the server.
Anything that matches no known field now goes to the banner instead of being
dropped, because an error in the wrong place beats an error nobody sees.

**Two — a validator stricter than the API.** The draft capped the author at
100 characters. `[StringLength(200, MinimumLength = 1)]` says 200. Nothing
fails loudly: the user types a long institutional attribution, the form
refuses it, and there is no way to discover that the server would have
accepted it. An invented limit is worse than a missing one, because it looks
deliberate.

**Three — whitespace passed the client and would have 400'd.** Neither the
draft nor the brief anticipated this one; it came out of reading the
framework source. Signal Forms' `required()` tests with `isEmpty()`, which is
`value === '' || value === false || value == null` — **it does not trim**.
The server's `RequiredAttribute` *does* trim before testing. So an author of
`"   "` is valid on the client, invalid on the server, and the user earns a
round-trip and a 400 for something the form could have caught before sending.
Fixed with an explicit `notOnlyWhitespace` validator on both fields,
reporting the same message `required()` would, because from the user's side
it is the same mistake.

Worth noting the mirror image, which the draft got right by accident:
`maxLength` in Signal Forms measures raw `.length`, and `StringLengthAttribute`
also does not trim — so those two agree. Had the client trimmed before
counting, 200 characters plus a trailing space would have passed the client
and been rejected by a server counting 201.

**Four — the accessible error path never ran, twice over.**

The draft put `aria-describedby` on the wrapping `<div class="field">`.
`aria-describedby` is announced for the control that has focus, and a `<div>`
is never focused — so the hint and the error message were announced to
nobody. The markup looked correct in a diff and read as silence. This is why
the spec asserts the attribute on `input#author` specifically, and separately
asserts the wrapper does **not** carry it: a test that only checked "the id
appears somewhere in the DOM" would have passed against the broken version.

Separately, the draft's `<form>` had no `novalidate`. Signal Forms writes a
native `required` attribute onto every control bound with `[formField]` —
confirmed in the compiled output, `renderer.setAttribute(element, 'required', '')`.
Without `novalidate` the browser runs its own validation first, shows its own
bubble, and cancels the submit event, so the error region, the ARIA wiring and
the focus move never execute at all. Two validation UIs competing, and the
inaccessible one winning.

## One bug I introduced while fixing it

The first version of `focusFirstInvalid()` queried
`'#author[aria-invalid="true"]'` and focus still went nowhere. `onInvalid`
runs inside `submit()`, synchronously, right after it marks every field
touched and *before* any change detection — so the signals said invalid while
the DOM had not been told yet, and the selector matched nothing. It now reads
field state and maps to element ids in DOM order, which is both correct and
synchronous. Asking the DOM a question only the next render can answer is an
easy mistake to make in a zoneless app, and it failed in exactly the same
silent way as the bugs it was written to fix.

Two other red tests were my own fault rather than the code's: the list refetch
after a successful create is issued by a reactive effect on a later tick, so
`expectOne` immediately after flushing the POST found nothing. That is a fact
about when resources re-fetch, and the fix was in the spec, not the component.

## The eighth failure, which is not a bug

`shows no errors before the user has done anything` also fails against the
draft, because the fix moved the alert and status regions into the DOM
permanently and merely hides them. A live region created at the same instant
it gains its text is announced unreliably across NVDA, JAWS and VoiceOver —
the assistive tech has to be observing the node before the mutation. So
"empty and present" is the wiring that actually announces, and the test now
asserts a hidden banner rather than an absent one. Counting that as a caught
bug would be flattering the review; it is a design change made during it.

## A fifth bug, found by running it rather than by the suite

Every state above was verified through `HttpTestingController`, and every one
of those tests passed. What they could not have caught: opening the actual
running form showed the "hidden" banner and success regions as two empty,
coloured, bordered boxes sitting under the submit button — not absent, not
invisible, just empty.

The cause is a CSS specificity tie the previous fix didn't anticipate.
`.error` and `.success` each set `display: flex` for the state where they
have content. The browser's own rule for a `hidden` attribute —
`[hidden] { display: none }` — lives in the user-agent stylesheet at the same
specificity, a single selector, as those class rules. A tie between an
author rule and a user-agent rule is won by the author rule, so `display:
flex` silently beat `display: none`, and "present but hidden" rendered as
"present, empty, and visibly boxed" instead. `[hidden]` was doing exactly
what it says on the tin — right up until an unrelated rule for the *other*
state outranked it.

Fixed by giving the hidden state its own, unambiguously higher-specificity
selector rather than fighting the tie: `.error.banner[hidden]` and
`.success[hidden]` in `quote-form.css`, both set to `display: none`
explicitly.

Worth being honest about what caught this and what didn't. `quote-form.spec.ts`
now asserts `getComputedStyle(...).display === 'none'` on both regions in the
pristine state — and that assertion does fail against the CSS this project
shipped for the `.success` region. It does *not* fail for `.banner` against
the same shipped CSS, because jsdom's handling of an author/user-agent
specificity tie is not fully spec-accurate: it resolves one of the two
identical bugs and not the other, for reasons internal to jsdom rather than
to the bug itself. The screenshot of the running page is the more reliable
witness here — both regions were visibly broken in the browser — and the
test is worth keeping because it locks in the fix for the half jsdom can see,
not because it is a complete substitute for looking at the rendered page.
That gap between "jsdom passed" and "the browser was still wrong" is the same
category of limitation `color-contrast` was already disabled for, just
showing up on a property jsdom claims to support rather than one it plainly
does not.

## What breaks if the quote contract changes

**A field is renamed.** `author` → `authorName` on the request would be
accepted by the endpoint as a *missing* `Author` — `[Required]` fires, the
response is a 400 whose `errors.Author` message is about a field the form
does not have under that name. The form would render "The Author field is
required" beside an author box the user has just filled in. Nothing in
TypeScript catches it: `CreateQuoteRequest` is erased at build time and the
server never sees the interface.

**A field is added and made required.** A new `[Required] Source` property
means every submission 400s with `errors.Source`. That key matches no field
in the form, so — after the fix — it surfaces in the banner rather than
vanishing, which is the difference between "the API is rejecting this for a
reason you can read" and a form that silently refuses to submit. Before the
fix it was dropped entirely.

**A length limit tightens.** `StringLength(1000)` → `StringLength(500)` on
`Text` leaves the client happily accepting 900 characters, and the server
rejecting them. The client-side `maxLength` is duplicated knowledge and will
go stale; what saves the user is that the server's own message now renders on
the right field. The honest fix is the one already written up for the `size`
cap in the Day 13 log — the API reporting its own limits rather than the
client mirroring them.

**The endpoint starts requiring auth.** Every POST returns 401, the form shows
"The API responded with HTTP 401", and there is no login to recover through.
Correct and useless, exactly as on the read path.

**The error payload shape changes.** If the server ever moves off
`ValidationProblemDetails` — a different envelope, or errors as an array of
`{field, message}` — `problem?.errors` is `undefined`, `fieldErrors` is `{}`,
and the form shows nothing at all on a 400. This is the same silent-blank
failure as finding one, and it is the reason the 400 branch is worth an
integration test on the client rather than trust: the parse succeeds either
way, and only an assertion about *rendered output* can tell the two apart.

## Signal Forms preview vs. Reactive Forms

Day 14 piece 2 asked for a short comparison against this same form, not a
second one built in parallel — where the preview API is simpler, where it is
still rough, and one over-claim worth checking rather than assuming. Written
up in full, with sources, in
[`SIGNAL-FORMS-VS-REACTIVE.md`](SIGNAL-FORMS-VS-REACTIVE.md).
