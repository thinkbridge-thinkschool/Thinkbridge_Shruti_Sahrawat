# The brief — create-a-quote form

The prompt given to the agent, before any code existed. Day 14, piece 1.

---

Build a `create-a-quote` form for the existing Angular 21 app in `quotes-ui/`,
posting to the Week-1 API I already wrote. Use **Signal Forms**
(`@angular/forms/signals`) — it is available in the installed
`@angular/forms@21.2.21` and it is what the rest of this app's state model
already looks like. No `ReactiveFormsModule`, no `NgModel`, no `NgModule`.

## The contract — transcribe it, do not infer it

The endpoint is `POST /api/quotes`. Everything below comes from
`QuotesApi/Models/QuoteDtos.cs` and
`QuotesApi/Extensions/EndpointExtensions.cs`. Read those files; do not guess
from the shape of the GET response.

**Request body** — exactly two fields, camelCase on the wire:

```json
{ "author": "Ada Lovelace", "text": "That brain of mine is something more than merely mortal." }
```

There is no `title`, no `tags`, no `source`, no `category`, no `id` on the
way in. `CreateQuoteRequest` has two properties and that is the whole form.
If you find yourself adding a third input, you have invented a field.

**Constraints**, from the data annotations on `CreateQuoteRequest`:

```csharp
[Required, StringLength(200,  MinimumLength = 1)] public string Author { get; set; }
[Required, StringLength(1000, MinimumLength = 1)] public string Text   { get; set; }
```

So: author 1–200, text 1–1000. Both required. **There is no minimum beyond
one character** — do not invent a "names must be at least 3 characters"
rule, because `Bo` and `Li` are real names and the API accepts them.

Two subtleties in those attributes that matter for where you put the
validation:

- `RequiredAttribute` on a string trims before testing, so `"   "` fails
  *required*, not *min-length*.
- `StringLengthAttribute` does **not** trim. It measures the raw string. So
  200 real characters plus two trailing spaces is 202 to the server and is
  rejected — even though `Quote.Create` would have trimmed it back to 200
  and been happy. If the client counts a trimmed length it will let through
  a value the API 400s on. Count what you are actually going to send.

**Success** is `201 Created` with the created quote:

```json
{ "id": 10001, "author": "…", "text": "…", "createdAt": "2026-08-24T09:30:00" }
```

**Validation failure** is `400` with an ASP.NET Core `ValidationProblemDetails`.
The `errors` dictionary is the part worth being careful about:

```json
{
  "type": "…", "title": "One or more validation errors occurred.", "status": 400,
  "errors": { "Author": ["The field Author must be a string with a minimum length of 1…"] }
}
```

**Those keys are `Author` and `Text` — capitalised.** Every other field in
this API is camelCase, and these are not, because they come from
`ValidationResult.MemberNames` (C# property names) into a dictionary, and
ASP.NET Core's web JSON defaults camel-case *property names* but not
*dictionary keys*. `Quotes.Tests.Integration/QuoteEndpointsTests.cs` asserts
`problem.Errors.Should().ContainKey("Author")` against a real SQL Server, so
this is checked behaviour and not my reading of the docs. Key off the
capitalised names, and map each one back onto the field it belongs to so the
message renders next to the input rather than in a heap at the top.

## States

Five, and I want each of them reachable and visibly distinct:

- **empty / pristine** — nothing typed, no errors shouting at a user who has
  not done anything wrong yet
- **invalid** — a field violates a rule, after the user has touched it or
  after a submit attempt
- **submitting** — request in flight; the form must not be double-submittable
- **server-error** — the API rejected it (400 with field errors, or a 500, or
  nothing answered at all — the three are different and read differently)
- **success** — 201; show what was created and let the user write another

## Accessibility — the actual point of the exercise

This must be operable and comprehensible without a mouse and without sight.

- Every input has a real `<label for>` pointing at its `id`. Not a
  placeholder standing in for a label, not `aria-label` papering over a
  missing one.
- `aria-invalid` on the control itself when that control is invalid — on the
  `<input>`/`<textarea>`, never on a wrapper.
- `aria-describedby` on the control, wiring it to both its hint text and its
  error message when one is showing. Screen readers announce `describedby`
  for the *focused control* — putting it on a surrounding `<div>` announces
  nothing to anyone. Make sure every id it references actually exists in the
  DOM at the moment it is referenced; a dangling id is read as silence.
- On a failed submit, **move focus to the first invalid control** and
  announce the failure. A visible error the keyboard never reaches is not an
  error the user gets to fix.
- The error summary and status messages live in polite live regions that
  exist in the DOM *before* they have content — a live region inserted at
  the same moment as its text is unreliably announced.
- Full keyboard path: tab to each field, type, submit with Enter from within
  the form, reach and operate every control including the reset/"write
  another" affordance.

One thing to be deliberate about: Signal Forms writes native `required` and
`maxlength` attributes onto the bound element. Decide what you want the
browser's own validation UI to do about that, and say why in a comment —
the native bubble and a custom accessible error region are two answers to
the same question and you should not ship both.

## Reasoning goes in comments

Why the server's error keys are capitalised. Why the length check counts
what it counts. What `aria-describedby` is pointing at and when. I will be
asked to defend these, and I want the argument next to the code rather than
in a document that drifts away from it.

---

## What I changed after reading the output

Written up in [`VERIFICATION-FORM.md`](VERIFICATION-FORM.md). Four things came
back wrong and I made the agent fix each: server errors keyed in the wrong
case so they never rendered, an author limit half what the API allows, an
`aria-describedby` on a wrapper `<div>` where it announces nothing, and a
missing `novalidate` that let the browser's native bubble pre-empt the
accessible error handling the rest of the brief asked for.
