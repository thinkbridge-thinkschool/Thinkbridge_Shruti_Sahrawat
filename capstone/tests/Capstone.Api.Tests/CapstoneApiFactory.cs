using Capstone.Curation.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Capstone.Api.Tests;

/// <summary>
/// The real API, booted in memory, over a database of its own.
/// </summary>
/// <remarks>
/// <b>What this is not.</b> It is not a second composition root. Program.cs
/// does all the wiring; this changes two settings and replaces one registration,
/// and everything else about the host under test - the DomainException
/// middleware, the module wiring, the background relay, the endpoint
/// definitions - is the code that ships. A test host that re-declares the
/// application's services proves that the re-declaration works.
///
/// <b>Why a file and not :memory:.</b> The infrastructure tests use an
/// in-memory SQLite database over one held-open connection, which is right
/// there: those tests are single-threaded and the connection is the database's
/// lifetime. Here it would be wrong twice over. A real host has a background
/// relay writing while request threads write, and a single SqliteConnection
/// shared across threads is not safe to use that way; and the contention
/// between those two writers is a property worth exercising rather than
/// designing away. A file per factory gives every test its own database, real
/// connection pooling, and the same locking behaviour the API has when it runs
/// for real.
/// </remarks>
internal sealed class CapstoneApiFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// Long enough that the relay drains once at startup and then does not run
    /// again for the lifetime of any test.
    /// </summary>
    /// <remarks>
    /// The endpoint tests below assert things like "the outbox row is staged
    /// and nobody has taken it yet", which is only a stable claim if no
    /// background thread is racing to take it. Parking the relay is how that
    /// assertion stays about the publish endpoint instead of about timing.
    /// <see cref="EndToEndPublishTests"/> is the one place that wants the relay
    /// awake, and asks for it explicitly.
    /// </remarks>
    private const int Parked = 60 * 60 * 1000;

    private readonly string _databasePath;
    private readonly int _pollIntervalMilliseconds;

    public CapstoneApiFactory(int relayPollIntervalMilliseconds = Parked)
    {
        _pollIntervalMilliseconds = relayPollIntervalMilliseconds;

        _databasePath = Path.Combine(
            Path.GetTempPath(), $"capstone-api-tests-{Guid.NewGuid():N}.db");

        // Migrated here rather than from inside the host, and the ordering is
        // the reason. Hosted services start when the host starts, so anything
        // that created the schema as a hosted service would have to start
        // before RelayHostedService - which Program.cs registers first, and
        // which this class has no business reordering. Building the context
        // directly needs no host at all, and it runs the same migration set
        // `dotnet ef database update` applies.
        //
        // Migrate, not EnsureCreated. EnsureCreated builds the schema from the
        // current model and never looks at the migrations, so it would answer
        // "do the tests pass" while leaving "do the migrations produce the
        // schema the model expects" untested - which is exactly the question
        // MigrationTests asks, and exactly the failure Day 24's Finding 17 and
        // Day 30's ORDER BY fix were both instances of.
        using var db = new CurationDbContext(
            new DbContextOptionsBuilder<CurationDbContext>()
                .UseSqlite(ConnectionString)
                .Options);

        db.Database.Migrate();
    }

    public string ConnectionString => $"Data Source={_databasePath}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not Development. WebApplicationFactory defaults to it, and the same
        // reasoning as Quotes.Tests.Integration applies: development defaults
        // exist to be chatty on one developer's screen, and a test run is not
        // one developer's screen.
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Relay:PollIntervalMilliseconds"] = _pollIntervalMilliseconds.ToString(),
                ["Logging:LogLevel:Default"] = "Warning",
                ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Warning",
            }));

        builder.ConfigureServices(services =>
        {
            // Both descriptors, not just the options one. AddDbContext is
            // additive: calling it twice chains every registered
            // IDbContextOptionsConfiguration<CurationDbContext> onto the same
            // builder rather than replacing it, so removing only
            // DbContextOptions<CurationDbContext> leaves Program.cs's UseSqlite
            // configuration action registered and both connection strings end
            // up attached to the final options.
            //
            // Not a guess: Quotes.Tests.Integration hit this in exactly this
            // shape and wrote the lesson down, which is the only reason it cost
            // nothing here.
            services.RemoveAll<DbContextOptions<CurationDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<CurationDbContext>>();

            services.AddDbContext<CurationDbContext>(options => options.UseSqlite(ConnectionString));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        // Microsoft.Data.Sqlite pools connections, and a pooled connection
        // still holds the file open after the host that opened it is gone.
        // Without this the delete below silently fails on Windows and every
        // test run leaves a database in the temp directory.
        SqliteConnection.ClearAllPools();

        // -wal and -shm are SQLite's write-ahead log and shared-memory index.
        // They are separate files, and deleting only the database leaves two
        // orphans behind per test.
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            TryDelete(_databasePath + suffix);
        }
    }

    /// <summary>
    /// Deletes if it can, and says nothing if it cannot.
    /// </summary>
    /// <remarks>
    /// Cleanup that throws turns a passing test into a failing one and sends
    /// whoever reads the log looking at the wrong thing. A temp file that
    /// outlives the run is a tidiness problem; a test suite that reports a
    /// failure it did not find is a correctness problem.
    /// </remarks>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
