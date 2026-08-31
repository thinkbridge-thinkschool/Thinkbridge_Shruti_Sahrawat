/**
 * The server's own limits on an account, transcribed rather than guessed at.
 *
 * Source of truth: QuotesApi/Models/User.cs (MaxEmailLength) and
 * QuotesApi/Models/AuthDtos.cs (PasswordRules). Repeated on the client for the
 * same reason quotes.ts repeats the quote limits: so the form can say what is
 * wrong before spending a round trip to be told. The server still enforces
 * them - this is a courtesy, not a defence - and if the two ever drift, the
 * symptom is a form that accepts something the API then rejects, which is why
 * the API's own field errors are rendered rather than swallowed.
 */

/** User.MaxEmailLength. */
export const EMAIL_MAX_LENGTH = 256;

/** PasswordRules.MinLength. */
export const PASSWORD_MIN_LENGTH = 8;

/**
 * PasswordRules.MaxLength.
 *
 * 72 is not an arbitrary cap: it is where BCrypt stops reading. The server
 * rejects anything longer rather than hashing a silently truncated prefix -
 * see the DTO for why quietly truncating would be the worse behaviour.
 */
export const PASSWORD_MAX_LENGTH = 72;
