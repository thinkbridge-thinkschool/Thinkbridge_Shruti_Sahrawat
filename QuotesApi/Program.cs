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

otel
    .ConfigureResource(r => r.AddService(
        serviceName: "QuotesApi",
        serviceVersion: "1.0.0"))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddEntityFrameworkCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddConsoleExporter()
        .AddOtlpExporter(o =>
        {
            o.Endpoint = new Uri("http://localhost:4317");
            o.Protocol = OtlpExportProtocol.Grpc;
        }));

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

builder.Services.AddHealthChecks();
builder.Services.AddControllers();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddScoped<ICollectionRepository, CollectionRepository>();
builder.Services.AddSingleton<IClock, SystemClock>();

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
app.MapQuoteEndpoints();
app.MapControllers();

app.Run();
