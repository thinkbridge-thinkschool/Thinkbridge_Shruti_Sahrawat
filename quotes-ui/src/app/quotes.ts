/**
 * The contract, transcribed from the API rather than guessed at.
 *
 * Source of truth: QuotesApi/Models/QuoteDtos.cs and
 * QuotesApi/Extensions/EndpointExtensions.cs.
 */

/** Mirrors QuoteResponse. */
export interface Quote {
  id: number;
  author: string;
  text: string;

  /**
   * String, not Date.
   *
   * ASP.NET Core serialises DateTime to an ISO-8601 string and nothing in the
   * HTTP layer revives it into a Date. Typing it as Date would compile, pass
   * review, and then blow up the first time anything called .getTime() on it.
   * DatePipe accepts the string, so nothing is lost.
   */
  createdAt: string;
}

/** Mirrors PagedResult<T>. The count field is totalCount, not total. */
export interface PagedResult<T> {
  items: T[];
  page: number;
  size: number;
  totalCount: number;
}

/**
 * The server's own limits, from EndpointExtensions.cs:
 *
 *   page = page <= 0 ? 1 : page;
 *   size = size <= 0 ? 10 : Math.Min(size, 100);
 *
 * Repeated on the client so the UI never sends a request it knows will be
 * silently rewritten. The server still clamps — this is a convenience, not a
 * defence, and the server remains the authority. If these ever drift, the
 * symptom is a page control that appears to do nothing.
 */
export const MIN_PAGE = 1;
export const MIN_SIZE = 1;
export const MAX_SIZE = 100;
export const DEFAULT_SIZE = 10;

/**
 * The state a single-quote detail lookup can be in — GET /api/quotes/{id}.
 *
 * A discriminated union rather than a bag of optional fields on its own. The
 * component that renders this switches on `status` and TypeScript narrows the
 * rest: reading `.quote` outside the `'ready'` branch is a compile error, not
 * a `Quote | undefined` the template has to remember to guard every time.
 *
 * `statusCode` is optional even on `'error'` — a request that never reached a
 * server (proxy down, DNS failure) has no HTTP status at all, the same
 * `statusCode() === undefined` distinction QuotesList already draws in
 * `failureKind`. Collapsing that into a fake `0` would be the same mistake
 * `totalCount ?? 0` was on Day 13 piece 1.
 */
export type DetailState =
  | { status: 'idle' }
  | { status: 'loading' }
  | { status: 'error'; statusCode?: number }
  | { status: 'ready'; quote: Quote };

// ---- POST /api/quotes ---------------------------------------------------

/**
 * Mirrors CreateQuoteRequest. Two fields, and only two.
 *
 * Note what is absent: no id (the server assigns it), no createdAt (the
 * server stamps it from IClock), no title/tags/source. The response type
 * `Quote` is deliberately *not* reused here — a request DTO and a response
 * DTO that happen to overlap today are still two contracts, and typing the
 * POST body as `Quote` would invite sending an `id` the endpoint ignores.
 */
export interface CreateQuoteRequest {
  author: string;
  text: string;
}

/**
 * The server's own limits, from the data annotations on CreateQuoteRequest:
 *
 *   [Required, StringLength(200,  MinimumLength = 1)] Author
 *   [Required, StringLength(1000, MinimumLength = 1)] Text
 *
 * StringLengthAttribute measures the raw string and does **not** trim, so
 * these count untrimmed characters — the same thing the request body will
 * carry. A client that validated `value.trim().length` would accept 200
 * characters plus a trailing space and be 400'd by a server counting 201.
 */
export const AUTHOR_MAX_LENGTH = 200;
export const TEXT_MAX_LENGTH = 1000;

/**
 * What a create attempt can come back as.
 *
 * `invalid` carries the server's own per-field messages rather than a
 * flattened string: the endpoint returns ValidationProblemDetails with an
 * `errors` dictionary, and those messages belong next to the field they are
 * about, not in a heap at the top of the form.
 */
export type CreateQuoteResult =
  | { outcome: 'created'; quote: Quote }
  | { outcome: 'invalid'; fieldErrors: Record<string, string[]> }
  | { outcome: 'failed'; statusCode?: number };
