import { parseQuoteId } from './quote-id';

/**
 * `parseQuoteId` is the whole fix for the Day 16 draft bug: the draft used
 * `Number(this.id())` with no validation, so `/quotes/abc` built the request
 * URL `/api/quotes/NaN` and sent it. Every case here is either something the
 * server's `{id:int}` constraint would accept (must parse back to the same
 * value) or something it would not (must be `null`, never reach a fetch).
 */
describe('parseQuoteId', () => {
  it('accepts a plain positive integer', () => {
    expect(parseQuoteId('42')).toBe(42);
  });

  it('accepts a leading-zero integer the same way Number() would', () => {
    expect(parseQuoteId('007')).toBe(7);
  });

  it('rejects the empty string', () => {
    expect(parseQuoteId('')).toBeNull();
  });

  it('rejects null and undefined', () => {
    expect(parseQuoteId(null)).toBeNull();
    expect(parseQuoteId(undefined)).toBeNull();
  });

  it('rejects non-numeric text — the exact case the draft bug sent to the API as NaN', () => {
    expect(parseQuoteId('abc')).toBeNull();
  });

  it('rejects zero and negative numbers — no quote is ever id 0 or below', () => {
    expect(parseQuoteId('0')).toBeNull();
    expect(parseQuoteId('-3')).toBeNull();
  });

  it('rejects a decimal', () => {
    expect(parseQuoteId('1.5')).toBeNull();
  });

  it('rejects whitespace and scientific notation, which Number() alone would silently accept', () => {
    expect(parseQuoteId(' 3 ')).toBeNull();
    expect(parseQuoteId('3e2')).toBeNull();
  });

  it('rejects an integer too large to be an exact JS number or a C# int', () => {
    expect(parseQuoteId('99999999999999999999')).toBeNull();
  });
});
