using Serilog;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Retry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Exporter;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using QuotesApi.Services;
using QuotesApi.Repositories;
using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Extensions;
using QuotesApi.Middleware;
using QuotesApi.BackgroundJobs;
using QuotesApi.Configuration;
using QuotesApi.Models;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using Microsoft.AspNetCore.HttpOverrides;
using Asp.Versioning;
using Asp.Versioning.Builder;

var builder = WebApplication.CreateBuilder(args);

// Day 27. Two limits Kestrel applies before a single line of application code
// runs, which is the only place they are worth applying.
builder.WebHost.ConfigureKestrel(kestrel =>
{
    // The largest legitimate body this API accepts is a quote: 200 characters
    // of author and 1000 of text, so about 1 KB. The default is 30 MB, which
    // is 30,000 times more than anything here needs and is read into memory
    // before validation ever sees it. 64 KB leaves generous room for a
    // collection payload and still makes a 30 MB POST free to reject.
    kestrel.Limits.MaxRequestBodySize = 64 * 1024;

    // `Server: Kestrel` tells an attacker what to look up. It tells a
    // legitimate caller nothing at all.
    kestrel.AddServerHeader = false;
});

// Day 27. Whether the demo, profiling, cache and resilience endpoints exist.
//
// They are genuinely useful - perf/breaker-timeline.ps1 drives them, the
// integration suite asserts on the profiling pair, and Days 12, 18, 21 and 22
// were all demonstrated through them - and they are also, collectively, the
// worst thing in this application's threat model: anonymous fault injection,
// an anonymous cache reset, an anonymous deliberately-slow query, and an
// anonymous endpoint that queues unbounded background work.
//
// The resolution is not to delete them but to make them local. The default is
// "on unless this is Production", which keeps every existing workflow intact:
// a laptop runs Development, the integration suite runs Testing, and only the
// deployed container runs Production. The explicit setting exists so the
// decision can be forced either way without editing code - including forcing
// them OFF in Development, which is how you check that the rest of the app
// does not secretly depend on them.
var diagnosticsEnabled =
    builder.Configuration.GetValue<bool?>("Diagnostics:Enabled")
    ?? !builder.Environment.IsProduction();

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}{NewLine}      TraceId={TraceId} {Message:lj}{NewLine}{Exception}"));

var appInsightsConnectionString =
    builder.Configuration["ApplicationInsights:ConnectionString"]
    ?? builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];

var otel = builder.Services.AddOpenTelemetry();

if (!string.IsNullOrWhiteSpace(appInsightsConnectionString))
{
    otel.UseAzureMonitor(o => o.ConnectionString = appInsightsConnectionString);
}

// Where to ship spans. Was a hardcoded http://localhost:4317, which is correct
// on exactly one machine and silently wrong everywhere else:
//
//   * In Azure there is no collector on localhost, so every span was exported
//     into nothing. Same failure mode as the App Insights connection-string bug
//     from Day 5 - no error, no log line, just no telemetry.
//   * Under `dotnet test` there is no collector either, but the failure is loud
//     rather than silent: every export attempt waits out its timeout and every
//     WebApplicationFactory disposal blocks on a final flush. The integration
//     suite went from seconds to 41 minutes.
//
// Now it comes from configuration, and no configured endpoint means no exporter.
var otlpEndpoint =
    builder.Configuration["Otel:OtlpEndpoint"]
    ?? builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

otel
    .ConfigureResource(r => r.AddService(
        serviceName: "QuotesApi",
        serviceVersion: "1.0.0"))
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation()
         .AddEntityFrameworkCoreInstrumentation()
         .AddHttpClientInstrumentation();

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            t.AddOtlpExporter(o =>
            {
                o.Endpoint = new Uri(otlpEndpoint);
                o.Protocol = OtlpExportProtocol.Grpc;
            });
        }

        // Writing every span to stdout is a debugging aid, not a deployment
        // strategy: it is synchronous console I/O on the request path.
        if (builder.Environment.IsDevelopment())
        {
            t.AddConsoleExporter();
        }
    });

// Day 5: named HttpClient with Polly-backed resilience (retry, circuit breaker,
// per-attempt timeout).
//
// Superseded by Day 22, and kept rather than deleted because Day 5's write-up
// cites this wiring and ResilienceHandlerTests still asserts against it -
// removing it would delete the evidence behind an earlier submission. It is
// left exactly as it was, including the two things Day 22 exists to fix: it
// retries every method, POST included, and its only timeout is per-attempt, so
// the caller has no total budget. Nothing in the application calls it; the
// outbound dependency now goes through IUpstreamClient.
builder.Services.AddHttpClient("my-service", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
    })
    .AddResilienceHandler("default", b =>
    {
        b.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromMilliseconds(200),
            OnRetry = args =>
            {
                Log.Warning(
                    "Retry {Attempt} after {Delay}ms due to {Outcome}",
                    args.AttemptNumber + 1,
                    args.RetryDelay.TotalMilliseconds,
                    args.Outcome.Exception?.Message
                        ?? args.Outcome.Result?.StatusCode.ToString());
                return ValueTask.CompletedTask;
            }
        });

        b.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(30),
            MinimumThroughput = 4,
            BreakDuration = TimeSpan.FromSeconds(15),
            OnOpened = args =>
            {
                Log.Error("Circuit breaker opened for {Duration}s", args.BreakDuration.TotalSeconds);
                return ValueTask.CompletedTask;
            },
            OnClosed = args =>
            {
                Log.Information("Circuit breaker closed");
                return ValueTask.CompletedTask;
            }
        });

        b.AddTimeout(TimeSpan.FromSeconds(10));
    });

builder.Services.AddMediatR(cfg =>
    cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));
builder.Services.AddHealthChecks();

// Day 27. See Extensions/RateLimitingExtensions.cs and ApiSurfaceExtensions.cs.
builder.Services.AddApiRateLimiting();
builder.Services.AddQuotesApiVersioning();
builder.Services.AddQuotesOpenApi();

// Container Apps terminates TLS at its ingress and forwards the original
// scheme and client address. Without this the app sees every request as HTTP
// from the ingress's own address, which would make the HSTS header never fire
// and would collapse every caller into a single rate-limit partition - one
// noisy client would then throttle everyone.
//
// Clearing the known networks and proxies means trusting whatever sends these
// headers. That is correct here because the ingress is the only route into
// the container, and it is worth stating plainly rather than leaving as a
// copied recipe: if this app were ever reachable directly, X-Forwarded-For
// would become caller-controlled and the rate limiter's partitioning would
// become caller-controlled with it.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddControllers();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddEndpointsApiExplorer();
// Day 21: HybridCache in front of GET /api/collections/summaries, with
// stampede protection so a cold cache under load issues one set of queries
// rather than one per concurrent request. Redis is optional - see
// Extensions/CachingExtensions.cs.
builder.Services.AddQuotesCaching(builder.Configuration);

// Day 22: the outbound dependency behind a Polly pipeline - bulkhead, total
// timeout, idempotent-only retry, circuit breaker, per-attempt timeout. See
// Extensions/ResilienceExtensions.cs for the wiring and
// Resilience/OutboundResiliencePipeline.cs for why the order is what it is.
builder.Services.AddOutboundResilience(builder.Configuration);

builder.Services.AddScoped<ICollectionRepository, CollectionRepository>();
builder.Services.AddSingleton<IClock, SystemClock>();

// Day 18: background jobs. One shared bounded queue, and the hosted
// service that drains it - see QuotesApi/BackgroundJobs/ for why each
// piece is shaped the way it is.
builder.Services.AddSingleton<IBackgroundTaskQueue>(_ => new BackgroundTaskQueue(capacity: 100));
builder.Services.AddHostedService<QueuedHostedService>();

// ---------------------------------------------------------------------------
// Authentication. Accounts own quotes; see Extensions/AuthEndpoints.cs.
// ---------------------------------------------------------------------------

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));

var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
var jwtOptions = jwtSection.Get<JwtOptions>() ?? new JwtOptions();

if (string.IsNullOrWhiteSpace(jwtOptions.Key))
{
    if (builder.Environment.IsProduction())
    {
        // Refuse to start rather than fall back to something.
        //
        // Any default here - a literal in this file, an empty string, a
        // "development" placeholder - is a key that is in the repository, and a
        // key in the repository lets anyone who can read it mint a token for
        // any account, admin included. A server that will not start is a
        // problem someone fixes in five minutes; a server running on a
        // published key is a problem nobody notices.
        throw new InvalidOperationException(
            "Jwt:Key is not configured. Set it as an environment variable (Jwt__Key) on the " +
            "container app before starting in Production.");
    }

    // Outside Production, generate one per process. Tokens then stop working
    // when the app restarts, which is mildly annoying locally and is the
    // correct trade: the alternative is a shared development key that
    // eventually gets copied into a real deployment.
    jwtOptions.Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    // Console, not Log.Warning. Serilog is configured inside UseSerilog, which
    // does not run until the host is built - a few lines below this. Anything
    // written through Log here goes to the silent default logger and is never
    // seen, which for a warning about key configuration is the worst possible
    // outcome.
    Console.WriteLine("[startup] WARNING: Jwt:Key was not configured. Generated an ephemeral key for " +
                      "this process - tokens will stop working when it restarts. Set it with " +
                      "`dotnet user-secrets set \"Jwt:Key\" \"<a long random string>\"` to avoid this.");
}

builder.Services.Configure<JwtOptions>(options =>
{
    options.Key = jwtOptions.Key;
    options.Issuer = jwtOptions.Issuer;
    options.Audience = jwtOptions.Audience;
    options.AccessTokenLifetime = jwtOptions.AccessTokenLifetime;
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            // Every one of these is on deliberately. Turning any of them off is
            // how a token that should have been rejected gets accepted:
            // an unvalidated lifetime accepts last year's token, an unvalidated
            // signing key accepts a token anyone minted, and an unvalidated
            // issuer or audience accepts a token minted for a different system
            // that happens to share a key.
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key)),

            // Stated rather than left to the default, because [Authorize(Roles
            // = ...)] and IsInRole read whichever claim type is named here. A
            // mismatch between what JwtTokenService writes and what this reads
            // does not fail loudly - it just means no user is ever in any role,
            // and the admin quietly sees what an ordinary user sees.
            NameClaimType = ClaimTypes.Name,
            RoleClaimType = ClaimTypes.Role,

            // Default is five minutes of leeway on expiry. An eight-hour token
            // does not need it, and it means a token tested as "expired" is
            // still accepted for another five minutes - which makes the expiry
            // test either slow or wrong.
            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
builder.Services.AddScoped<ITokenService, JwtTokenService>();
builder.Services.AddScoped<IUserRepository, UserRepository>();

var app = builder.Build();

// Forwarded headers first: everything downstream that asks "was this HTTPS?"
// or "who is calling?" gets the real answer only after this has run.
app.UseForwardedHeaders();

// Security headers before anything that can short-circuit, so a 401, a 429 and
// an unhandled 500 all carry them too. A header only present on the happy path
// is a header missing exactly when a response is most interesting.
app.UseSecurityHeaders();

app.UseSerilogRequestLogging();
app.UseCorrelationId();
app.UseExceptionHandling();

// Before authentication on purpose: rejecting a flood should not first cost a
// signature validation and a database lookup.
app.UseRateLimiter();

// Authentication before authorization, and both before any endpoint runs.
// Reversed, authorization would run against an anonymous principal that
// authentication has not filled in yet - and every [Authorize] endpoint would
// reject every request, including correctly signed ones.
app.UseAuthentication();
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();

    // How the schema arrives is a fact about the deployment, not about the
    // provider, so it is an explicit setting like Database:Provider rather
    // than something inferred - and for the same reason: an inference that
    // is right in production and wrong everywhere else fails silently.
    //
    // Migrate() is the default and the right answer wherever a migration set
    // for the live provider is on hand. That is SQLite everywhere so far, and
    // it is also Quotes.Tests.Integration, which points this context at real
    // SQL Server and supplies its own SQL-Server-native migrations from the
    // test assembly.
    //
    // Azure SQL is the exception. No SQL-Server migration set ships inside
    // this project - the one the integration tests prove lives in that test
    // assembly, which is not part of the deployed image - so EnsureCreated()
    // builds the schema straight from the model there, at the cost of not
    // tracking migration history for this provider. A fair trade for a
    // database this API is not yet evolving incrementally in production.
    //
    // Inferring EnsureCreated() from "the provider is SQL Server" is what
    // broke the integration suite: EnsureCreated() is a no-op when the
    // database already exists, and each test creates its own throwaway
    // database before the host starts. Every table was therefore missing and
    // every test failed in setup, in Program.cs rather than in test code.
    var schemaBootstrap = app.Configuration["Database:SchemaBootstrap"]
                          ?? (db.Database.IsSqlServer() ? "EnsureCreated" : "Migrate");

    if (string.Equals(schemaBootstrap, "EnsureCreated", StringComparison.OrdinalIgnoreCase))
    {
        db.Database.EnsureCreated();
    }
    else if (string.Equals(schemaBootstrap, "Migrate", StringComparison.OrdinalIgnoreCase))
    {
        db.Database.Migrate();
    }
    else
    {
        // Loud rather than falling back to a default. A typo here silently
        // decides whether the schema exists at all.
        throw new InvalidOperationException(
            $"Database:SchemaBootstrap was '{schemaBootstrap}'. Expected 'Migrate' or 'EnsureCreated'.");
    }
}

app.MapHealthChecks("/health");

// Day 27. One version set, applied to the two real endpoint groups. Callers
// that send no version get 1.0, which is every caller that exists today - see
// Extensions/ApiSurfaceExtensions.cs for why versioning had to be additive.
var versionSet = app.NewApiVersionSet()
    .HasApiVersion(new ApiVersion(1, 0))
    .ReportApiVersions()
    .Build();

app.MapAuthEndpoints(versionSet);
app.MapQuoteEndpoints(versionSet);
app.MapControllers();

// Day 27. Everything below exists to demonstrate or diagnose this app, and
// none of it belongs on a public host. In Production this block does not
// register, so the routes do not 401 - they 404, because they are not there.
if (diagnosticsEnabled)
{
    // The generated contract is a review artifact, not a runtime feature. It
    // lists every route including the ones above, so publishing it from a
    // production host would hand an attacker the map for free.
    app.MapOpenApi();

    app.MapProfilingEndpoints();
    app.MapCacheDiagnosticsEndpoints();

    // Day 22. The stub upstream is the dependency the pipeline calls; the
    // diagnostics endpoints are what perf/breaker-timeline.ps1 drives and reads.
    app.MapUpstreamStubEndpoints();
    app.MapResilienceDiagnosticsEndpoints();

    // Demo endpoint: forces transient failures so the Polly retry logs are visible.
    app.MapGet("/api/demo/resilience", async (IHttpClientFactory factory, CancellationToken ct) =>
    {
        var client = factory.CreateClient("my-service");
        try
        {
            var response = await client.GetAsync("http://localhost:9/always-fails", ct);
            return Results.Ok(new { status = (int)response.StatusCode });
        }
        catch (Exception ex)
        {
            // Never silently swallowed: the failure is logged and surfaced as 503.
            Log.Error(ex, "Call to my-service failed after all retries");
            return Results.Problem(
                detail: ex.GetType().Name + ": " + ex.Message,
                statusCode: 503,
                title: "Downstream call failed after retries");
        }
    });
    // Demo endpoint: enqueues slow work and returns immediately, proving the
    // request thread never blocks on it. The queued work has nothing real to
    // compute - it just sleeps and logs - so what it demonstrates is the
    // handoff itself, not any particular job.
    app.MapPost("/api/demo/queue-work", async (IBackgroundTaskQueue queue, int delayMs) =>
    {
        await queue.QueueBackgroundWorkItemAsync(async token =>
        {
            Log.Information("Background work item started, will run for {DelayMs}ms", delayMs);
            await Task.Delay(delayMs, token);
            Log.Information("Background work item finished");
        });

        return Results.Accepted(value: new { queued = true, delayMs });
    });
}

app.Run();
