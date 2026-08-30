[← Back to full README](../../README.md)

## Day 14 — Reactive forms and accessibility

[`quotes-ui/quote-form.ts`](../../quotes-ui/src/app/quote-form.ts) — a create-a-quote
form against `POST /api/quotes`, built with **Signal Forms**
(`@angular/forms/signals`, preview in Angular 21) rather than
`ReactiveFormsModule`: the field tree is signals, like everything else on this
screen.

Same three artefacts as Day 13: [`BRIEF-FORM.md`](../../quotes-ui/BRIEF-FORM.md) is
the prompt, [`quote-form.ts`](../../quotes-ui/src/app/quote-form.ts) is what came
back, and [`VERIFICATION-FORM.md`](../../quotes-ui/VERIFICATION-FORM.md) is what
happened when it was run.

Accessibility is the actual exercise, not a checklist at the end: `<label for>`
on every control, `aria-invalid` and `aria-describedby` **on the control
itself**, live regions that exist before they have content, and focus moved to
the first invalid field on a failed submit.

**Four bugs, caught by a spec written against the brief and run at the draft.**
The same file unchanged gives **8 failures against the draft and 21 passes
against the fix**.

The one worth the space is the contract bug. This API returns validation
errors as `errors: { "Author": [...] }` — capitalised — while every other
field it serialises is camelCase, because the keys are C# property names in a
`Dictionary` and ASP.NET Core camel-cases property names but not dictionary
keys. That is checked behaviour, not a reading of the docs:
[`QuoteEndpointsTests.cs`](../../Quotes.Tests.Integration/QuoteEndpointsTests.cs)
asserts `problem.Errors.Should().ContainKey("Author")` against real SQL Server.
A form reading `errors.author` parses that 400 cleanly, throws nothing, logs
nothing, and renders no errors at all — indistinguishable from success until
you notice the quote was never created.

The other three: an author capped at 100 where the API allows 200; whitespace
passing `required()` on the client because Signal Forms' `isEmpty()` does not
trim while `RequiredAttribute` does; and `aria-describedby` on a wrapping
`<div>`, where it is announced to nobody, on a form with no `novalidate` — so
the browser's native bubble pre-empted the submit event and none of the
accessible error handling ran.

The write-up also records a bug introduced *during* the fix — focus management
that queried `[aria-invalid="true"]` from inside `onInvalid`, before change
detection had written the attribute — and two red tests that turned out to be
the spec's fault rather than the component's.

**Verified by axe-core and DOM assertions, not by a screen reader.** There is
no live Week-1 API and no assistive tech in the environment this was built in,
so what is proven is that the ARIA contract is correct, not that NVDA reads it
as intended. `VERIFICATION-FORM.md` says so in those terms.

**A fifth bug, found after submission by looking at the running page.** The
banner and success regions are permanently in the DOM and only ever
`[hidden]`, by design — but `.error` and `.success` in `quote-form.css` both
set `display: flex`, at the exact same CSS specificity as the browser's own
`[hidden] { display: none }` rule. That tie went to the stylesheet, not the
browser, so the pristine form showed two empty, coloured, bordered boxes
where nothing should render at all. No test caught it — jsdom does not
reproduce that specificity tie the way a real browser does. Fixed with
`.error.banner[hidden]` / `.success[hidden]`, both explicit `display: none`,
and a `getComputedStyle` regression test added afterward — one that,
honestly, only catches half of the original bug in jsdom, which
`VERIFICATION-FORM.md` says plainly rather than overclaiming.

## Day 14, piece 2 — Signal Forms preview against Reactive Forms

[`SIGNAL-FORMS-VS-REACTIVE.md`](../../quotes-ui/SIGNAL-FORMS-VS-REACTIVE.md) is the
comparison this piece asked for, against the same real form above rather than
a second component built in parallel. Simpler: no `FormBuilder`/`FormGroup`
double declaration of the model's shape, one `[formField]` directive instead
of `formControlName` plus a manual invalid-check, validators declared next to
the field they constrain. Rougher, checked against the installed
`@angular/forms/signals` package rather than assumed: async validation is
Promise-based and newer, dynamic field groups go through plain signal arrays
with a thinner set of real-world examples than `FormArray`, third-party
`ControlValueAccessor` controls do not plug in natively, and the API is
`@experimental` in the Angular version this project is pinned to — it did not
reach stable until Angular 22.

The over-claim worth naming: it would be easy to say Signal Forms'
`required()` not trimming whitespace is a preview-API shortcoming Reactive
Forms doesn't share. Read directly from both packages, `Validators.required`
and `required()` use the same emptiness check, and neither trims. The
whitespace bug fixed above is a gap the two APIs share, not one specific to
the preview.
