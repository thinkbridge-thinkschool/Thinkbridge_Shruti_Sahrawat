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

var builder = WebApplication.CreateBuilder(args);

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

// Named HttpClient with Polly-backed resilience (retry, circuit breaker, timeout).
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
builder.Services.AddControllers();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddEndpointsApiExplorer();
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

app.UseSerilogRequestLogging();
app.UseCorrelationId();
app.UseExceptionHandling();

// Authentication before authorization, and both before any endpoint runs.
// Reversed, authorization would run against an anonymous principal that
// authentication has not filled in yet - and every [Authorize] endpoint would
// reject every request, including correctly signed ones.
app.UseAuthentication();
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();

    // SQL Server in production has no migrations of its own shipped in this
    // project - the SQL-Server-native migration set proven by
    // Quotes.Tests.Integration lives in that test assembly, which is not part
    // of the deployed image. EnsureCreated() builds the schema directly from
    // the current model instead, which sidesteps needing that assembly here
    // at the cost of not tracking migration history for this provider - a
    // fair trade for a database this API is not yet evolving incrementally
    // in production. SQLite (everywhere so far) keeps using Migrate(),
    // unchanged.
    if (db.Database.IsSqlServer())
    {
        db.Database.EnsureCreated();
    }
    else
    {
        db.Database.Migrate();
    }
}

app.MapHealthChecks("/health");

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
app.MapAuthEndpoints();
app.MapQuoteEndpoints();
app.MapProfilingEndpoints();
app.MapControllers();

app.Run();
