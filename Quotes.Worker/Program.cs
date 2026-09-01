using Microsoft.EntityFrameworkCore;
using Quotes.Messaging;
using Quotes.Messaging.Data;
using Quotes.Messaging.Publishing;
using Quotes.Worker;

// One executable, three verbs, because the exercise needs a consumer, something
// to drive it, and something to inspect the result - and three separate
// projects sharing one configuration file and one database would be more
// ceremony than the demo is worth.
//
//   dotnet run                -> run as a competing consumer (start two of these)
//   dotnet run -- publish     -> publish the demo message set
//   dotnet run -- dlq         -> print what is sitting in the dead-letter queues
//   dotnet run -- purge-dlq   -> drain both dead-letter queues (reset between runs)
//   dotnet run -- report      -> print the projections and the idempotency ledger
var verb = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "worker";

// The verb is stripped before the host sees it. Host.CreateApplicationBuilder
// feeds args to the command-line configuration provider, which rejects bare
// positional values - it expects --key=value - so passing "publish" through
// would throw before any of this ran.
var builder = Host.CreateApplicationBuilder(args.Length > 0 ? args[1..] : args);

var settings = new ServiceBusSettings();
builder.Configuration.GetSection(ServiceBusSettings.SectionName).Bind(settings);
builder.Services.AddSingleton(settings);

var databaseConnectionString =
    builder.Configuration["Database:ConnectionString"] ?? "Data Source=quotes-messaging.db";

builder.Services.AddDbContext<MessagingDbContext>(
    options => options.UseSqlite(databaseConnectionString),
    // Scoped is the default, but stating it is the point: SubscriptionProcessor
    // opens a scope per message so no two messages ever share a context.
    contextLifetime: ServiceLifetime.Scoped);

// One client for the process. It owns the AMQP connection; everything else
// borrows senders and receivers from it.
builder.Services.AddSingleton(_ => ServiceBusClientFactory.Create(settings));

// Names the instance in logs and in the ledger's ProcessedBy column, so which
// instance won a given message is visible in the data rather than inferred.
var instanceId = Environment.GetEnvironmentVariable("WORKER_INSTANCE")
                 ?? $"pid-{Environment.ProcessId}";
builder.Services.AddSingleton(new WorkerInstance(instanceId));

if (verb == "worker")
{
    builder.Services.AddHostedService<SubscriptionWorker>();
}

var host = builder.Build();

// Both the worker and the one-shot verbs need the schema to exist. EnsureCreated
// rather than migrations: this database is a rebuildable projection plus a
// ledger, not a system of record with a history worth versioning - deleting the
// file and replaying the topic is the intended recovery path.
await using (var scope = host.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

    // Retried, not a single best-effort call. Two worker instances started
    // together both race to create the same schema on the same file the
    // instant the process comes up. EnsureCreated is check-then-create - not
    // itself atomic - so the loser of that race can hit SQLITE_BUSY or a
    // "table already exists" failure on a brand-new database, and crashing
    // the whole process over a startup race it was always going to lose one
    // side of would be exactly the kind of silent, hard-to-diagnose failure
    // this exercise is otherwise all about avoiding. A short retry lets the
    // winner finish and the loser's next attempt find the schema already
    // there and succeed as a no-op.
    var attempt = 0;
    while (true)
    {
        try
        {
            await db.Database.EnsureCreatedAsync();
            break;
        }
        catch (Exception ex) when (attempt < 5)
        {
            attempt++;
            Console.Error.WriteLine(
                $"Schema creation attempt {attempt} hit {ex.GetType().Name} (likely racing another instance); retrying...");
            await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt));
        }
    }

    // WAL lets a reader and a writer coexist instead of blocking each other,
    // which matters here because two worker processes share this one file.
    // Best-effort: WAL is a performance property, not a correctness one, so a
    // failure to set it must not stop the worker from starting.
    try
    {
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Could not enable WAL ({ex.GetType().Name}); continuing in default journal mode.");
    }
}

switch (verb)
{
    case "worker":
        await host.RunAsync();
        break;

    case "publish":
        await DemoPublisher.RunAsync(host.Services);
        break;

    case "dlq":
        await DeadLetterInspector.RunAsync(host.Services);
        break;

    case "purge-dlq":
        await DeadLetterInspector.PurgeAsync(host.Services);
        break;

    case "report":
        await StateReporter.RunAsync(host.Services);
        break;

    default:
        Console.Error.WriteLine($"Unknown verb '{verb}'. Use: worker | publish | dlq | purge-dlq | report");
        return 1;
}

return 0;
