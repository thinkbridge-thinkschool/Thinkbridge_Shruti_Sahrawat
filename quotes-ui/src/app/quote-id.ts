/**
 * Parses a route's `:id` segment into the positive integer the server's own
 * route constraint expects, or `null` if it isn't one.
 *
 * `GET /api/quotes/{id:int}` in `EndpointExtensions.cs` — the `:int`
 * constraint means ASP.NET Core's routing does not match this endpoint at
 * all for a non-integer segment. A request to `/api/quotes/abc` never
 * reaches `GetById`'s own handler, never gets its `ProblemDetails` 404 with
 * a `title`/`detail` this app knows how to render — it falls through to
 * ASP.NET's generic routing 404 instead, an empty body this app's error
 * handling was never built to read.
 *
 * Angular's router draws no equivalent line. `path: 'quotes/:id'` matches
 * *any* string in that segment — `/quotes/abc`, `/quotes/-3`, `/quotes/1.5`
 * all land on QuoteDetail exactly like `/quotes/42` does, with the raw
 * string handed to the component as its `id` input. Left unchecked, the
 * component would build `/api/quotes/${id}` straight from that string and
 * send whatever it got. This is the boundary that stops that: reject
 * anything that is not a plain positive integer *before* a request is ever
 * made, rather than let the server's differently-shaped 404 reach code that
 * doesn't expect it.
 */
export function parseQuoteId(raw: string | null | undefined): number | null {
  if (!raw) return null;
  // Anchored, digits-only — Number('') is 0, Number(' 3 ') is 3, Number('3e2')
  // is 300, and Number(undefined) is NaN: three different ways a looser check
  // (Number.isInteger(Number(raw))) would accept input the server's {id:int}
  // binder would not, or silently reinterpret whitespace/scientific notation
  // as a different id than the one actually in the URL.
  if (!/^\d+$/.test(raw)) return null;

  const id = Number(raw);
  // Number.isSafeInteger, not just a truthiness check — a segment such as
  // '99999999999999999999' passes the regex above but overflows what both
  // a JS number and a C# int can represent exactly.
  return Number.isSafeInteger(id) && id > 0 ? id : null;
}
