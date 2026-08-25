# Signal Forms preview vs. Reactive Forms

A short comparison, grounded in the form already built for Day 14 piece 1:
[`quote-form.ts`](src/app/quote-form.ts), posting to the real
`POST /api/quotes` endpoint with its actual two fields, `Author`
(`[Required, StringLength(200, MinimumLength = 1)]`) and `Text`
(`[Required, StringLength(1000, MinimumLength = 1)]`).

This is not a second component built in parallel — the brief for this task
accepts a hand-coded version already in progress, and the Signal Forms form
above is that version. What follows is what changed writing it this way
instead of with `ReactiveFormsModule`, and where the preview API is still
rough, checked against the installed package (`@angular/forms/signals`,
Angular 21.2.21) rather than assumed.

## Where it is simpler

There is no `FormBuilder`, no `FormGroup`, no `FormControl`, and no second
type declaration for what the form holds — `form(this.model, (path) => ...)`
takes a plain signal of `{ author: string; text: string }` and the field
tree's shape follows from it. Reactive Forms needs the shape declared twice,
once in the model and once in `FormGroup<{...}>`, and the two can drift.

The template binds with one directive, `[formField]`, in place of
`formControlName` plus a manual `(ngSubmit)` handler that reads
`this.form.value` and checks `this.form.invalid`. Field state —
`touched()`, `dirty()`, `valid()`, `errors()` — is signals, read the same
way every other piece of state in this app is read, rather than an
`Observable`-backed `AbstractControl` API sitting next to a zoneless,
signal-first codebase. `submit()` marks every field touched, skips the
`action` callback entirely when the form is invalid, and hands back a
promise — the imperative "check `.invalid`, bail early" is gone.

Validators read like the constraint they express: `required(path.author)`,
`maxLength(path.author, AUTHOR_MAX_LENGTH)`, next to the field they apply
to, instead of an array of functions passed into `FormControl`'s
constructor and matched up by position.

## Where it is still rough

Checked against `@angular/forms/signals`'s own exports rather than assumed:
`required`, `maxLength`, `minLength`, `min`, `max`, `email`, and `pattern`
all exist as of this version, so the common validators are not the gap —
that assumption would have been wrong if left unchecked.

What is genuinely thinner, per Angular's own framing and independent
write-ups of this same release: async validation (`validateAsync`,
`validateHttp`) is Promise-based, a deliberate break from Reactive Forms'
`AsyncValidatorFn`, and teams with `switchMap`/`combineLatest`-heavy
validation chains will feel that shift; dynamic, repeating field groups —
Reactive Forms' `FormArray` territory — go through plain signal arrays
instead of a dedicated array type, with a much newer and thinner set of
real-world examples to check a pattern against; and components built on
`ControlValueAccessor`, which is most of the third-party Angular form-control
ecosystem, do not plug in natively and need Signal Forms' own custom-control
mechanism instead. None of these apply to this form — two flat string
fields, no async per-field check, no third-party control — which is itself
worth saying plainly: this project didn't hit the preview API's rough edges
because it never asked enough of it to.

The label matters too. This is `@experimental` in the Angular version this
project is pinned to (21.2); Signal Forms did not reach stable status until
Angular 22, and the API surface changed between the two — a production
codebase adopting it now is choosing an API that may still move.

## The over-claim worth naming explicitly

It would be easy to write "Signal Forms' `required()` doesn't trim
whitespace, so a Reactive Forms version wouldn't have shipped that bug" —
and it would be wrong. Read directly from the installed packages: Reactive
Forms' `Validators.required` checks emptiness with `isEmptyInputValue`,
which tests only `value == null` and length; Signal Forms' `required()`
checks with `isEmpty()`, doing the same thing. Neither trims. `"   "` passes
both. The whitespace bug fixed in `quote-form.ts` (finding 3 in
[`VERIFICATION-FORM.md`](VERIFICATION-FORM.md)) is not a preview-API
shortcoming relative to Reactive Forms — it is a gap both APIs share, and
claiming otherwise would have been the over-claim this comparison was asked
to watch for.

## Sources

Angular's own labeling and the exported symbols were checked directly
against the installed `@angular/forms` package rather than assumed. The
framing of what changed between Angular 21's preview and Angular 22's stable
release, and the specific gaps described above (async validation, dynamic
field arrays, `ControlValueAccessor` integration, `@experimental` status),
draw on:

- [Stable Signal Forms in Angular 22: What Changed](https://houseofangular.io/stable-signal-forms-in-angular-22-what-changed/)
- [Angular Signal Forms vs Reactive Forms: an honest analysis](https://pavel-iakupov.medium.com/angular-signal-forms-vs-reactive-forms-an-honest-analysis-a94dad62c811)
