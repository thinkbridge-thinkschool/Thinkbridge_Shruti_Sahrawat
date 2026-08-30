[← Back to full README](../../README.md)

## Day 3 — Enterprise Auth and Testing

**Wire Entra ID as the identity provider** — verified end to end. Full account in [`docs/ENTRA-VERIFICATION.md`](../../OrderRefactor/docs/ENTRA-VERIFICATION.md).
[`OrderRefactor/Program.cs`](../../OrderRefactor/Program.cs) registers two bearer schemes behind a policy scheme:
Request with Bearer token
↓
PolicyScheme reads the issuer claim (reads only — no validation yet)
↓
iss == "OrderRefactorIssuer"?
├─ yes → InternalJwt symmetric key, my own tokens
└─ no → EntraJwt Microsoft's public keys, fetched from Authority
↓
[Authorize] resolves as normal — controllers unchanged
Entra configuration (`TenantId`, `ClientId`, `Audience`) lives in `appsettings.json`; these are public identifiers, not secrets. Authority is `https://login.microsoftonline.com/{tenant}/v2.0`. No client secret is needed anywhere — an API that only validates tokens uses Microsoft's published signing keys.
A real Microsoft-signed token now authenticates against this API. `GET /api/orders/whoami` returns 401 with no token, 401 with a malformed one, and 200 with an Entra token whose issuer is `https://login.microsoftonline.com/{tenant}/v2.0` and which the policy scheme routes to `EntraJwt`. Microsoft issued it, the handler fetched the signing keys from the published JWKS endpoint and verified the signature asymmetrically, and audience and lifetime both checked out.

**The honest caveat:** that was proven in a personal Entra directory where I am Global Administrator, not in the institutional thinkbridge tenant, which still rejects the `access_as_user` grant with `AADSTS65005` because granting it needs an admin I am not. I routed around the blocker rather than through it. What is proven is that the code validates Entra tokens correctly; what is not proven is that the thinkbridge registration is configured. The difference between them is three values in `appsettings.json` and no code at all.

Two things nearly broke it silently, both producing a 401 that looks like a signing failure. A default registration issues **v1** tokens whose issuer is `sts.windows.net`, which fails against a v2 `Authority` — and would not even reach the Entra validator, since `IssuerSchemeSelector` matches on the `login.microsoftonline.com` prefix and would fall through to the internal scheme. And with `requestedAccessTokenVersion = 2` the `aud` claim is the bare client-id GUID rather than `api://{client-id}`; the value in config was set by decoding the token rather than by assuming.

What *is* proven is the routing decision, which used to be an untestable lambda inside `Program.cs`: reaching it meant booting the app and letting the Entra handler make a live call to `login.microsoftonline.com` for its key set. It now lives in [`IssuerSchemeSelector`](../../OrderRefactor/Authentication/IssuerSchemeSelector.cs) with [20 tests](../../OrderRefactor.Tests/IssuerSchemeSelectorTests.cs) covering every branch in microseconds, no network involved.

Extracting it surfaced two defects. The lambda compared the issuer against a hardcoded `"OrderRefactorIssuer"` while the validator it routes to reads `ValidIssuer` from configuration — rename `Jwt:Issuer` and every internally-issued token would have been routed to the Entra validator and rejected. And it matched `"Bearer "` case-sensitively, which RFC 7235 says is wrong. Both are now regression-tested.

**Authorization policies and claims**
`AdminOnly` (claim-based) and `CanEditOwnOrders` (custom assertion) defined in [`Program.cs`](../../OrderRefactor/Program.cs), applied at [`OrderController.CreateOrder`](../../OrderRefactor/Controllers/OrderController.cs). Authentication answers *who you are*; policies answer *what you may do*. Roles are claims that change; policies encode rules that don't.

**Lock down the API end-to-end**
[`OrderControllerTests.cs`](../../OrderRefactor.Tests/OrderControllerTests.cs) + [`RefreshTokenTests.cs`](../../OrderRefactor.Tests/RefreshTokenTests.cs) — 21 tests: anonymous → 401, authenticated but wrong policy → 403, correct policy → 201, expired token → 401, malformed token → 401, revoked refresh chain → 401.

**The testing pyramid in real terms**
Reflected in the actual shape of the suites: 164 unit tests running in about a second between them, 43 integration tests taking over two minutes. The lesson that stuck is that the pyramid is about *time*, not test count — 43 integration tests consume roughly a hundred times the wall-clock of 164 unit tests, so the ratio that matters is the one on the stopwatch.

Writing `ExceptionHandlingMiddlewareTests` made the point from the other direction. That middleware had no coverage at all, and no integration test could have given it any: nothing in a healthy request throws, so a global exception handler is only reachable by handing it something that fails. A unit test with a hostile `RequestDelegate` does that in microseconds. The code was not hard to test — the tier was wrong.

**xUnit with FluentAssertions**
[`Quotes.Tests.Unit/`](../../Quotes.Tests.Unit) — one test class per production class, `Method_StateUnderTest_ExpectedBehavior` naming, explicit AAA in every test, no `SetUp` hiding arrangement, `[Theory]`/`[InlineData]` for boundaries. NSubstitute for `IOrderRepository`, `IConfiguration`, and `ILogger<T>`.

**Integration tests with WebApplicationFactory**
[`Quotes.Tests.Integration/`](../../Quotes.Tests.Integration) — 23 tests booting the real application: real middleware pipeline, real DI graph, real EF. A fresh database and `HttpClient` per test, no shared state between tests. `ProblemDetails` and `ValidationProblemDetails` response shapes are asserted, not just status codes.

**Real SQL Server in CI with Testcontainers**
[`MsSqlContainerFixture.cs`](../../Quotes.Tests.Integration/MsSqlContainerFixture.cs) — one SQL Server 2022 container per assembly run via `IAsyncLifetime` + `ICollectionFixture`, with each test getting its own database on that shared container. The suite goes 2s → 14s: the honest cost of testing against a real engine.
The SQLite migrations could not be replayed against SQL Server. They bake in literal `TEXT` column types and a `Sqlite:Autoincrement` annotation that SQL Server silently ignores, producing a table with no `IDENTITY` — so every insert fails. The fix is not translation but a separate SQL-Server-native migration set inside the test project ([`Migrations/SqlServer/`](../../Quotes.Tests.Integration/Migrations/SqlServer)), wired via `MigrationsAssembly`. Zero production changes; the SQLite app still runs unmodified.

---
