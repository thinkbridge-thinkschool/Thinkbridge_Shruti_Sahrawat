import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import axe from 'axe-core';
import { errorMappingInterceptor } from './error-mapping';
import { QuoteForm } from './quote-form';
import { AUTHOR_MAX_LENGTH, TEXT_MAX_LENGTH } from './quotes';

/**
 * Exercises the create-a-quote form against a mocked POST /api/quotes.
 *
 * Two things this file is deliberately built to catch, because reading the
 * diff would not have:
 *
 * 1. **The server's error-key casing.** The 400 bodies flushed below use
 *    `errors: { Author: [...] }` — capitalised — because that is what the
 *    API actually returns. It is not a guess:
 *    `Quotes.Tests.Integration/QuoteEndpointsTests.cs` asserts
 *    `problem.Errors.Should().ContainKey("Author")` against a real SQL
 *    Server. A client keyed on `errors.author` parses that body without
 *    error and renders nothing, which looks identical to "the server
 *    accepted it" right up until the list fails to refresh.
 *
 * 2. **The ARIA wiring.** Assertions here query for the attribute *on the
 *    control* (`input#author`), not merely somewhere in the field's markup.
 *    `aria-describedby` on a wrapping <div> is inert — a screen reader
 *    announces the description of the focused control, and a <div> is never
 *    focused — so a test that only asserted "the id appears in the DOM"
 *    would pass against markup that reads nothing to anybody.
 *
 * There is no live Week-1 API in this environment and no screen reader, so
 * a11y is verified two ways instead: axe-core over the rendered DOM, and
 * direct assertions on the attributes a screen reader would consume.
 *
 * `errorMappingInterceptor` is wired in here — not just left to its own
 * `error-mapping.spec.ts` — because `QuotesApi.createQuote` now depends on
 * it running: it opts the POST into `MAP_ERRORS` and classifies the
 * `AppError` the interceptor throws, not a raw `HttpErrorResponse`. Testing
 * this component without the interceptor its production `app.config.ts`
 * always runs alongside would pass against a shape the real app never
 * produces.
 */
describe('QuoteForm — POST /api/quotes', () => {
  let fixture: ComponentFixture<QuoteForm>;
  let component: QuoteForm;
  let httpMock: HttpTestingController;
  let el: HTMLElement;

  async function settle(): Promise<void> {
    await Promise.resolve();
    await Promise.resolve();
    fixture.detectChanges();
  }

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([errorMappingInterceptor])),
        provideHttpClientTesting(),
        // The template's new "View it" link (Day 16) is a routerLink, and
        // RouterLink injects Router at construction — with no router
        // provided at all this component would fail to even create, not
        // just render the link inertly. Empty route table: nothing here
        // actually navigates.
        provideRouter([]),
      ],
    });

    fixture = TestBed.createComponent(QuoteForm);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
    el = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
    await settle();

    // QuotesApi's list resource fires on construction — unrelated to the
    // POST under test, but a real request httpMock.verify() would flag.
    httpMock
      .expectOne((req) => req.url.startsWith('/api/quotes?'))
      .flush({ items: [], page: 1, size: 10, totalCount: 0 });
    await settle();
  });

  afterEach(() => httpMock.verify({ ignoreCancelled: true }));

  // ---- driving the form the way a person does --------------------------

  const authorInput = () => el.querySelector<HTMLInputElement>('#author')!;
  const textInput = () => el.querySelector<HTMLTextAreaElement>('#text')!;
  const formEl = () => el.querySelector<HTMLFormElement>('form')!;
  const submitButton = () => el.querySelector<HTMLButtonElement>('button[type="submit"]')!;

  async function type(control: HTMLInputElement | HTMLTextAreaElement, value: string) {
    control.value = value;
    control.dispatchEvent(new Event('input'));
    await settle();
  }

  async function blur(control: HTMLElement) {
    control.dispatchEvent(new Event('blur'));
    await settle();
  }

  async function fillValid() {
    await type(authorInput(), 'Ada Lovelace');
    await type(textInput(), 'That brain of mine is something more than merely mortal.');
  }

  /** Submits and returns the promise so a test can inspect the in-flight state. */
  function startSubmit(): Promise<void> {
    const pending = component.onSubmit(new Event('submit'));
    fixture.detectChanges();
    return pending;
  }

  /**
   * Drains the list refetch a successful create triggers.
   *
   * It is not synchronous with the POST response: reloadList() marks the
   * list resource stale, and the re-request is issued by a reactive effect
   * on a later tick. Asserting expectOne() straight after flushing the POST
   * finds nothing — which is a fact about when resources re-fetch, not a
   * defect, and cost two red tests before it was understood.
   */
  async function drainListReload(): Promise<void> {
    await settle();
    for (const req of httpMock.match((r) => r.url.startsWith('/api/quotes?'))) {
      req.flush({ items: [], page: 1, size: 10, totalCount: 1 });
    }
    await settle();
  }

  const validationProblem = (errors: Record<string, string[]>) => ({
    type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
    title: 'One or more validation errors occurred.',
    status: 400,
    errors,
  });

  // ---- states ----------------------------------------------------------

  it('shows no errors before the user has done anything', () => {
    // The alert/status regions are deliberately always in the DOM and
    // merely hidden — a live region created at the same moment it gains
    // text is announced unreliably. So "no errors" is about rendered field
    // messages and a hidden banner, not about the region being absent.
    expect(el.querySelector('#author-error')).toBeNull();
    expect(el.querySelector('#text-error')).toBeNull();
    expect(el.querySelector<HTMLElement>('.banner')!.hidden).toBe(true);
    expect(authorInput().getAttribute('aria-invalid')).toBeNull();
    expect(textInput().getAttribute('aria-invalid')).toBeNull();
  });

  it('actually hides the banner and success regions visually, not just via the DOM property', () => {
    // The `.hidden` IDL property only says the `hidden` attribute is
    // present — it says nothing about whether a stylesheet overrides the
    // browser's own `[hidden] { display: none }` rule. `.error` and
    // `.success` both set `display: flex` for when they *are* shown, at the
    // same specificity as that rule, so an unqualified `[hidden]` loses the
    // tie and the "hidden" box renders as an empty, bordered, coloured bar —
    // which is exactly what showed up in the running app, on top of the fix
    // this same commit made for the four bugs in VERIFICATION-FORM.md.
    //
    // Run against the CSS this project actually shipped for a few hours,
    // this assertion caught the `.success` half (jsdom returned `flex`) but
    // not the `.banner` half — jsdom's cascade tie-breaking between an
    // author rule and a same-specificity UA rule is not fully spec-accurate,
    // so it under-reproduces one of the two identical bugs rather than
    // over-claiming a false positive. The screenshot that actually caught
    // both is the more trustworthy source here; this test locks in the fix
    // for whichever half jsdom can see, which is better than nothing but
    // not a substitute for looking at the rendered page.
    const banner = el.querySelector<HTMLElement>('.banner')!;
    const success = el.querySelector<HTMLElement>('.success')!;
    expect(getComputedStyle(banner).display).toBe('none');
    expect(getComputedStyle(success).display).toBe('none');
  });

  it('reports a required field as invalid once it has been touched', async () => {
    await type(authorInput(), 'x');
    await type(authorInput(), '');
    await blur(authorInput());

    expect(component.quoteForm.author().invalid()).toBe(true);
    expect(authorInput().getAttribute('aria-invalid')).toBe('true');
    expect(el.querySelector('#author-error')?.textContent).toContain('required');
  });

  it('disables the submit button while the request is in flight', async () => {
    await fillValid();

    const pending = startSubmit();
    await settle();

    expect(submitButton().disabled).toBe(true);
    expect(submitButton().textContent).toContain('Saving');

    httpMock.expectOne('/api/quotes').flush({
      id: 1,
      author: 'Ada Lovelace',
      text: 'That brain of mine is something more than merely mortal.',
      createdAt: '2026-08-25T09:30:00',
    });
    await pending;
    await drainListReload();

    expect(submitButton().disabled).toBe(false);
  });

  it('reports success and clears the fields after a 201', async () => {
    await fillValid();
    const pending = startSubmit();
    await settle();

    httpMock.expectOne('/api/quotes').flush({
      id: 10001,
      author: 'Ada Lovelace',
      text: 'That brain of mine is something more than merely mortal.',
      createdAt: '2026-08-25T09:30:00',
    });
    await pending;
    await drainListReload();

    expect(component.created()?.id).toBe(10001);
    expect(el.querySelector('.success')?.textContent).toContain('Ada Lovelace');
    expect(authorInput().value).toBe('');
    expect(textInput().value).toBe('');
  });

  it('surfaces a non-validation failure as a distinct banner, not as field errors', async () => {
    await fillValid();
    const pending = startSubmit();
    await settle();

    httpMock
      .expectOne('/api/quotes')
      .flush({ title: 'Server error' }, { status: 500, statusText: 'Server Error' });
    await pending;
    await settle();

    expect(el.querySelector('.banner')?.textContent).toContain('500');
    expect(component.quoteForm.author().errors().length).toBe(0);
  });

  // ---- the contract ----------------------------------------------------

  it('renders server field errors keyed the way the API actually returns them', async () => {
    // Capitalised keys — verified against QuoteEndpointsTests.cs, which
    // asserts problem.Errors.ContainKey("Author") against real SQL Server.
    await fillValid();
    const pending = startSubmit();
    await settle();

    httpMock.expectOne('/api/quotes').flush(
      validationProblem({
        Author: ['The field Author must be a string with a maximum length of 200.'],
        Text: ['The Text field is required.'],
      }),
      { status: 400, statusText: 'Bad Request' },
    );
    await pending;
    await settle();

    expect(
      component.quoteForm
        .author()
        .errors()
        .map((e) => e.message),
    ).toContain('The field Author must be a string with a maximum length of 200.');
    expect(
      component.quoteForm
        .text()
        .errors()
        .map((e) => e.message),
    ).toContain('The Text field is required.');
    expect(el.querySelector('#author-error')?.textContent).toContain('maximum length of 200');
  });

  it('accepts an author of exactly the length the API allows', async () => {
    // [StringLength(200, MinimumLength = 1)] — 200 is valid, so a UI that
    // rejects it is refusing input the server would have taken.
    await type(authorInput(), 'a'.repeat(AUTHOR_MAX_LENGTH));
    await blur(authorInput());

    expect(component.quoteForm.author().errors()).toEqual([]);
    expect(component.quoteForm.author().valid()).toBe(true);
  });

  it('rejects a whitespace-only author, which the server rejects as missing', async () => {
    // RequiredAttribute trims before testing, so "   " fails Required
    // server-side. Signal Forms' required() uses isEmpty(), which only
    // catches '' — so without an explicit check the client posts a value
    // it should have caught, and eats an avoidable 400.
    await type(authorInput(), '   ');
    await blur(authorInput());

    expect(component.quoteForm.author().valid()).toBe(false);
  });

  // ---- accessibility ---------------------------------------------------

  it('associates every control with a real label', () => {
    for (const id of ['author', 'text']) {
      const label = el.querySelector<HTMLLabelElement>(`label[for="${id}"]`);
      expect(label).not.toBeNull();
      expect(label!.textContent!.trim().length).toBeGreaterThan(0);
    }
  });

  it('puts aria-describedby on the control itself, where it is announced', async () => {
    // Not on the wrapping <div>: describedby is announced for the *focused*
    // control, and a div is never focused, so the description reads as
    // silence to a screen-reader user while looking wired up in the DOM.
    expect(authorInput().getAttribute('aria-describedby')).toContain('author-hint');
    expect(textInput().getAttribute('aria-describedby')).toContain('text-hint');

    expect(el.querySelector('div.field')?.hasAttribute('aria-describedby')).toBe(false);
  });

  it('extends aria-describedby to the error message once one is showing', async () => {
    await type(authorInput(), '');
    await blur(authorInput());

    const describedBy = authorInput().getAttribute('aria-describedby') ?? '';
    expect(describedBy).toContain('author-error');

    // Every id it points at must exist, or the reference reads as nothing.
    for (const id of describedBy.split(/\s+/).filter(Boolean)) {
      expect(el.querySelector(`#${id}`)).not.toBeNull();
    }
  });

  it('suppresses the browser bubble so the accessible error path is what runs', () => {
    // Signal Forms writes a native `required` attribute onto the bound
    // control. Without novalidate the browser's own validation UI fires
    // first on submit, blocks the submit event, and none of the error
    // display or focus management below ever runs.
    expect(authorInput().hasAttribute('required')).toBe(true);
    expect(formEl().hasAttribute('novalidate')).toBe(true);
  });

  it('moves focus to the first invalid control when submit fails validation', async () => {
    await type(textInput(), 'Text without an author.');

    await startSubmit();
    await settle();

    expect(document.activeElement).toBe(authorInput());
    // No request should have left the browser.
    httpMock.expectNone('/api/quotes');
  });

  it('has no axe violations in its default state', async () => {
    const results = await axe.run(el, {
      // jsdom has no layout, so contrast cannot be computed here; it is
      // checked separately against the token palette in styles.css.
      rules: { 'color-contrast': { enabled: false } },
    });

    expect(results.violations.map((v) => `${v.id}: ${v.help}`)).toEqual([]);
  });

  it('has no axe violations while showing errors', async () => {
    await type(authorInput(), '');
    await blur(authorInput());
    await type(textInput(), '');
    await blur(textInput());

    const results = await axe.run(el, {
      rules: { 'color-contrast': { enabled: false } },
    });

    expect(results.violations.map((v) => `${v.id}: ${v.help}`)).toEqual([]);
  });
});
