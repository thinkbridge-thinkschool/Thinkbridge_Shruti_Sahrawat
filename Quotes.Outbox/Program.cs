using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Microsoft.EntityFrameworkCore;
using Quotes.Messaging;
using Quotes.Messaging.Publishing;
using Quotes.Outbox;

// This process has exactly one job: read OutboxMessages rows QuotesApi
// already committed, and publish the unsent ones. It owns no schema (see
// OutboxDbContext) and constructs no long-lived state QuotesApi did not
// already create - restarting it, or scaling it to zero for an hour, changes
// nothing about correctness, only about how quickly unsent rows drain.
var builder = Host.CreateApplicationBuilder(args);

// Same two settings, same names, as QuotesApi.Extensions.InfrastructureExtensions
// - deliberately, so pointing this relay at the right database is "copy the
// same two lines of configuration QuotesApi already has", not a second,
// differently-shaped setting to keep in sync by hand.
var connectionString = builder.Configuration.GetConnectionString("Default") ?? "Data Source=quotes.db";
var provider = builder.Configuration["Database:Provider"] ?? "Sqlite";
var useSqlServer = string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase);

builder.Services.AddDbContext<OutboxDbContext>(options =>
{
    if (useSqlServer)
    {
        options.UseSqlServer(connectionString);
    }
    else
    {
        options.UseSqlite(connectionString);
    }
});

var serviceBusSettings = new ServiceBusSettings();
builder.Configuration.GetSection(ServiceBusSettings.SectionName).Bind(serviceBusSettings);
builder.Services.AddSingleton(serviceBusSettings);

// One client for the process, same reasoning as Quotes.Worker: it owns the
// AMQP connection and everything else borrows senders from it.
builder.Services.AddSingleton(_ => ServiceBusClientFactory.Create(serviceBusSettings));

builder.Services.AddSingleton<IQuoteEventPublisher>(sp => new ServiceBusQuoteEventPublisher(
    sp.GetRequiredService<Azure.Messaging.ServiceBus.ServiceBusClient>(),
    serviceBusSettings,
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<ServiceBusQuoteEventPublisher>()));

// ---------------------------------------------------------------------------
// Day 26 - telemetry.
//
// This process is the middle of the trace, and the only reason it can be:
// OutboxRelay reads the traceparent QuotesApi stored on the row and starts its
// publish span underneath it. Without an exporter here that span is created
// and discarded, and App Insights sees an API trace and a worker trace with
// nothing joining them.
//
// AddSource("Azure.*") is what captures the Service Bus SDK's own spans. Those
// are the spans that carry trace context onto (and off) the wire, so without
// this line the process still reports its own work and still stitches to
// nothing - the most misleading possible half-success, because telemetry
// appears to be working.
// ---------------------------------------------------------------------------

// Belt and braces. Azure SDK distributed tracing was gated behind this switch
// while it was experimental; it is on by default in current versions
// (Azure.Messaging.ServiceBus 7.18.2 here), so this is expected to be a no-op.
// It is set anyway because the failure it prevents - no Service Bus spans at
// all, therefore no stitched trace - is silent, and the cost of setting a
// switch nothing reads is nothing.
AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);

var appInsightsConnectionString =
    builder.Configuration["ApplicationInsights:ConnectionString"]
    ?? builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: "Quotes.Outbox",
        serviceVersion: "1.0.0"))
    .WithTracing(t =>
    {
        t.AddSource("Azure.*")
         .AddSource(OutboxRelay.ActivitySource.Name)
         .AddEntityFrameworkCoreInstrumentation();

        // No connection string means no exporter, exactly as QuotesApi does
        // it: a process that cannot reach App Insights should run normally and
        // export nothing, not fail to start and not spend every span on a
        // doomed network call.
        if (!string.IsNullOrWhiteSpace(appInsightsConnectionString))
        {
            t.AddAzureMonitorTraceExporter(o => o.ConnectionString = appInsightsConnectionString);
        }
    });


builder.Services.AddScoped<OutboxRelay>();

var pollInterval = builder.Configuration.GetValue<TimeSpan?>("Outbox:PollInterval") ?? TimeSpan.FromSeconds(5);
var batchSize = builder.Configuration.GetValue<int?>("Outbox:BatchSize") ?? 20;

builder.Services.AddHostedService(sp => new OutboxRelayHostedService(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<ILogger<OutboxRelayHostedService>>(),
    pollInterval,
    batchSize));

var host = builder.Build();

// A readiness probe, not a migration. This relay does not own the
// OutboxMessages table - QuotesApi's migrations do - so it must never call
// EnsureCreated or Migrate here; either would let this process silently
// invent a schema QuotesApi's own migrations disagree with the next time
// they run. What it can safely do is ask whether the table it needs is
// already there and fail loudly, at startup, if it is not - a relay that
// started successfully against a database with no outbox table would sit in
// its poll loop forever looking healthy while draining nothing.
await using (var scope = host.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OutboxDbContext>();
    try
    {
        await db.Outbox.AnyAsync();
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException(
            "Could not read the OutboxMessages table. Run QuotesApi's own migrations against this " +
            "database first - this relay does not create schema; see OutboxDbContext for why.", ex);
    }
}

await host.RunAsync();
