/**
 * Validators shared by the sign-in and register forms.
 *
 * Same shape as `notOnlyWhitespace` in quote-form.ts: a function taking the
 * field context and returning either an error object or null. Kept in their
 * own module rather than duplicated into both pages, since the two forms ask
 * the same questions of the same two fields.
 */

/**
 * Rejects a value that is only whitespace.
 *
 * The server's [Required] trims before testing, so "   " fails it, while
 * Signal Forms' required() only catches '' and null. Without this the client
 * lets whitespace through and earns an avoidable 400.
 */
export function notOnlyWhitespace(message: string) {
  return (ctx: { value: () => string }) =>
    ctx.value().length > 0 && ctx.value().trim().length === 0
      ? { kind: 'required', message }
      : null;
}

/**
 * A deliberately loose email check: one @, something either side, no spaces.
 *
 * Not a full RFC 5322 parse, and not one of the notorious thousand-character
 * regexes either. The only question worth answering on the client is "is this
 * obviously not an address" - the server re-validates with [EmailAddress], and
 * the real proof that an address exists is that mail sent to it arrives, which
 * no regex can establish. An over-strict pattern rejects valid addresses -
 * plus-tags, new TLDs, apostrophes - and gives the user no way to argue.
 */
export function looksLikeEmail(message: string) {
  return (ctx: { value: () => string }) => {
    const value = ctx.value().trim();
    if (value.length === 0) return null; // required() owns the empty case.
    return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value) ? null : { kind: 'email', message };
  };
}

/**
 * Mirrors PasswordRules.MinLength.
 *
 * Skipped while the field is empty, so an untouched form shows "a password is
 * required" rather than two errors saying the same thing in different words.
 */
export function atLeast(min: number, message: string) {
  return (ctx: { value: () => string }) =>
    ctx.value().length > 0 && ctx.value().length < min ? { kind: 'minLength', message } : null;
}
