[← Back to full README](../../README.md)

## Day 2 — Architecture and Authentication

**Piece 1 — Dependency injection at depth**
[`QuotesApi/Services/IClock.cs`](../../QuotesApi/Services/IClock.cs) · [`OrderRefactor/Services/IClock.cs`](../../OrderRefactor/Services/IClock.cs)
All three lifetimes, chosen rather than defaulted: `DbContext` and the repositories **scoped**, because the change tracker is a per-request unit of work and is not thread-safe; `DiscountCalculator` **transient**, because it is stateless and sharing an instance buys nothing; `IClock` **singleton**, because it holds nothing at all. The classic failure this avoids is the captive dependency — a singleton holding a scoped `DbContext` and quietly corrupting state across requests.

No production code path reads the ambient clock any more. The domain does not depend on `IClock` either: `Quote.Create` and `Collection.AddItem` take a `DateTimeOffset`, and the application layer — the endpoint handler, the MediatR handler, `AuthController`, `OrderService` — owns the clock and passes the instant down. An entity that goes looking for the time cannot be asserted against exactly, and every timestamp test here now asserts an exact value instead of `BeCloseTo(DateTime.UtcNow, 5 seconds)`.

Proven end to end rather than by inspection: [`CreateQuote_WithClockOverridden_PersistsTheClockInstantAndReadsItBack`](../../Quotes.Tests.Integration/QuoteEndpointsTests.cs) overrides `IClock` in the test host, posts a quote, and reads the row back out of SQL Server to confirm the injected instant was stored — not merely echoed in the response.

**Piece 2 — async/await with cancellation through layers**
`CancellationToken` is the last parameter on every I/O method and flows controller → service → repository → EF. See [`IOrderRepository.cs`](../../OrderRefactor/Repositories/IOrderRepository.cs) and [`QuoteRepository.cs`](../../QuotesApi/Repositories/QuoteRepository.cs). Cancellation is tested, not assumed: [`CollectionsControllerCancellationTests.cs`](../../OrderRefactor.Tests/CollectionsControllerCancellationTests.cs).

**Piece 3 — Test the domain layer**
[`OrderRefactor.Tests/CollectionDomainTests.cs`](../../OrderRefactor.Tests/CollectionDomainTests.cs), extended considerably in [`Quotes.Tests.Unit/CollectionTests.cs`](../../Quotes.Tests.Unit/CollectionTests.cs) — every `Collection` invariant including the 49th/50th/51st item boundary. Pure and fast: no DbContext, no fixtures, no setup methods.

**Piece 4 — AI-assisted refactor: anemic to rich**
[`QuotesApi/Models/Quote.cs`](../../QuotesApi/Models/Quote.cs) — private setters, private constructor, a static `Quote.Create` factory validating author (1–200 chars) and text (1–1000 chars) with trimming, and `SoftDelete()` instead of a publicly mutable flag. Rationale and the bug the anemic version would have shipped: [`WHY.md`](../../WHY.md). Tests: [`Quotes.Tests.Unit/QuoteTests.cs`](../../Quotes.Tests.Unit/QuoteTests.cs).

**Piece 5 — JWT auth with my own issuer**
[`OrderRefactor/Controllers/AuthController.cs`](../../OrderRefactor/Controllers/AuthController.cs) — `POST /api/auth/login` returns `access_token`, `refresh_token`, and `expires_in`. HS256, signed with a 256-bit key read from configuration, never hardcoded.

**Piece 6 — Refresh tokens with rotation and reuse detection**
Same file. Refresh tokens are stored hashed, never in plaintext ([`Models/RefreshToken.cs`](../../OrderRefactor/Models/RefreshToken.cs): `TokenHash`, `UserId`, `ExpiresAt`, `RevokedAt`, `ReplacedByToken`). Every refresh rotates the pair and marks the old token replaced. **Presenting an already-rotated token revokes the entire family for that user** and forces re-authentication — so a leaked token cannot be used twice, and the theft is detected rather than silently exploited.
Proven end-to-end in [`RefreshTokenTests.cs`](../../OrderRefactor.Tests/RefreshTokenTests.cs): log in, refresh once, replay the spent token → 401, then confirm the legitimate user's current token is dead too.

---
