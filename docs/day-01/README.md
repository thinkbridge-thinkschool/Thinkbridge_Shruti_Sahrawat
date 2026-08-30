[← Back to full README](../../README.md)

## Day 1 — Foundations

**Piece 1 — Hello in two languages**
[`hello-cs/Program.cs`](../../hello-cs/Program.cs) · [`hello-ts/hello.ts`](../../hello-ts/hello.ts)
C# needs a `.csproj` and an SDK before it will run anything; Node 24 executes TypeScript directly with no build step and no config file. The contrast is the point — one runtime asks you to declare structure up front, the other asks for nothing.

**Piece 2 — Minimal ASP.NET Core API**
[`QuotesApi/Extensions/EndpointExtensions.cs`](../../QuotesApi/Extensions/EndpointExtensions.cs) · [`QuotesApi/Repositories/QuoteRepository.cs`](../../QuotesApi/Repositories/QuoteRepository.cs)
Four endpoints on `/api/quotes` — paged list, create, get by id, delete. EF Core + SQLite with migrations applied at startup, scoped `IQuoteRepository` via DI, `ValidationProblemDetails` on invalid input, `CancellationToken` flowing into every EF query, structured logging via `ILogger<T>`, and `ProblemDetails` from [exception middleware](../../QuotesApi/Middleware/ExceptionHandlingMiddleware.cs). `Program.cs` stays under 120 lines by splitting into `AddInfrastructure()` and `MapQuoteEndpoints()` extension methods.

**Piece 3 — Refactor a god-method controller**
Before: [`OrderRefactor/Original/OrderController.cs`](../../OrderRefactor/Original/OrderController.cs) — the ~250-line original, saved unmodified.
Prompt that generated it: [`OrderRefactor/Original/PROMPT.md`](../../OrderRefactor/Original/PROMPT.md)
Analysis: [`OrderRefactor/REFACTOR_NOTES.md`](../../OrderRefactor/REFACTOR_NOTES.md) — 10+ distinct smells, each with its consequence and intended fix, written before touching a line of code.
After: [`Controllers/OrderController.cs`](../../OrderRefactor/Controllers/OrderController.cs) → [`Services/OrderService.cs`](../../OrderRefactor/Services/OrderService.cs) → [`Repositories/`](../../OrderRefactor/Repositories) — split into layers wired by DI, async end-to-end with cancellation, typed return shapes, and empty catches replaced with narrow handlers that log and rethrow.

**Piece 4 — Real AI-assisted work**
[`AI_REFLECTION.md`](../../AI_REFLECTION.md) — where Claude Code helped, where it over-engineered and I pushed back, and where Copilot suggested something subtly wrong.

**Piece 5 — Build a real aggregate**
[`QuotesApi/Domain/Collection.cs`](../../QuotesApi/Domain/Collection.cs) · [`QuotesApi/Domain/CollectionItem.cs`](../../QuotesApi/Domain/CollectionItem.cs)
An aggregate root that enforces its own invariants: name 3–80 characters, maximum 50 items, no duplicate quote IDs, positive quote ID required. `CollectionItem` is an immutable value object mapped as an EF owned type. Every mutation goes through `AddItem`/`RemoveItem`, which throw rather than letting callers touch the collection directly — so the aggregate is consistent after every operation, not merely by convention. Endpoints: [`CollectionsController.cs`](../../QuotesApi/Controllers/CollectionsController.cs).

---
