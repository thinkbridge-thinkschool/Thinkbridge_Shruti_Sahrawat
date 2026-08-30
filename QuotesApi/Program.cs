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

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseCorrelationId();
app.UseExceptionHandling();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();
    db.Database.Migrate();
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
app.MapQuoteEndpoints();
app.MapProfilingEndpoints();
app.MapControllers();

app.Run();
