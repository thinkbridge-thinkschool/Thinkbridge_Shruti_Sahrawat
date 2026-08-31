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

  /**
   * Who created it, or null for a quote that predates accounts existing.
   *
   * Published by the API so an admin — who sees everyone's quotes — can tell
   * which rows are their own. It is not what decides whether the delete button
   * does anything: the server re-checks ownership on every delete, because a
   * client deciding what it may do is a client that can decide differently.
   */
  ownerId: number | null;
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

// ---- GET /api/quotes/{id} -------------------------------------------------
//
// No shared DetailState type here as of Day 16 — the state a single-quote
// lookup can be in now lives next to the one place that reads it,
// QuoteDetail itself, since the fetch moved there too (route-driven, not
// store-driven; see quotes-store.ts). It also grew one more case than the
// old union had: 'invalid', for a route :id that never should have reached
// a fetch at all — see quote-id.ts.

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
