using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QuotesApi.Data;
using QuotesApi.Services;

namespace Quotes.Tests.Integration;

// Shared test-host plumbing only — no business "arrangement" lives here. Every test still
// calls CreateFreshHost() explicitly as its own first Arrange step, so nothing runs implicitly
// before a test the way an xUnit constructor/IClassFixture would. Each call creates a brand-new
// database on the shared SQL Server container (see MsSqlContainerFixture) and a brand-new
// WebApplicationFactory, so tests never share state. Migrations applied are the SQL-Server-native
// set under Migrations/SqlServer (scaffolded fresh from the current model), not QuotesApi's
// SQLite migrations — those bake in literal "TEXT"/"INTEGER" store types and a Sqlite-only
// autoincrement annotation that don't produce a working schema on SQL Server.
internal static class TestInfrastructure
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<TestHost> CreateFreshHost(MsSqlContainerFixture sqlServer, IClock? clock = null)
    {
        var databaseName = $"quotes_test_{Guid.NewGuid():N}";

        var masterBuilder = new SqlConnectionStringBuilder(sqlServer.MasterConnectionString);
        await using (var masterConnection = new SqlConnection(masterBuilder.ConnectionString))
        {
            await masterConnection.OpenAsync();
            await using var createDbCommand = masterConnection.CreateCommand();
            createDbCommand.CommandText = $"CREATE DATABASE [{databaseName}]";
            await createDbCommand.ExecuteNonQueryAsync();
        }

        var testDbBuilder = new SqlConnectionStringBuilder(sqlServer.MasterConnectionString)
        {
            InitialCatalog = databaseName
        };

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Not Development. WebApplicationFactory defaults to it, which switched on
            // EnableSensitiveDataLogging, LogTo(Console.WriteLine) and the OpenTelemetry
            // console exporter - thousands of lines of synchronous console I/O per test,
            // for output nobody reads unless something fails.
            builder.UseEnvironment("Testing");

            // Serilog is configured inside UseSerilog, which runs when the host is
            // built, so overrides added here do reach it. Anything Program.cs reads
            // at *builder* time does not — see TestEnvironment, which clears the
            // telemetry exporters out of the environment instead.
            //
            // appsettings.json puts EF's SQL logging at Debug. That is the right
            // default for local development and ruinous across a few hundred
            // per-test databases: thousands of lines of synchronous console I/O.
            // Raise it temporarily if you are debugging a failing test.
            builder.ConfigureAppConfiguration((context, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Serilog:MinimumLevel:Default"] = "Warning",
                    ["Serilog:MinimumLevel:Override:Microsoft"] = "Warning",
                    ["Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore"] = "Warning",
                    ["Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore.Database.Command"] = "Warning"
                });
            });

            builder.ConfigureServices(services =>
            {
                // AddDbContext is additive across calls: it chains every registered
                // IDbContextOptionsConfiguration<QuotesDbContext> (Program.cs's UseSqlite included)
                // onto the same builder rather than replacing it. Removing only the
                // DbContextOptions<QuotesDbContext> descriptor leaves the SQLite configuration
                // action registered, so both providers end up attached to the final options and
                // EF throws "Only a single database provider can be registered". Strip both.
                services.RemoveAll<DbContextOptions<QuotesDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<QuotesDbContext>>();
                services.AddDbContext<QuotesDbContext>(options => options.UseSqlServer(
                    testDbBuilder.ConnectionString,
                    x => x.MigrationsAssembly(typeof(TestInfrastructure).Assembly.FullName)));

                if (clock is not null)
                {
                    var clockDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IClock));
                    if (clockDescriptor != null)
                    {
                        services.Remove(clockDescriptor);
                    }
                    services.AddSingleton(clock);
                }
            });
        });

        return new TestHost(factory.CreateClient(), factory);
    }
}

internal sealed class TestHost : IDisposable
{
    public HttpClient Client { get; }
    public WebApplicationFactory<Program> Factory { get; }

    public TestHost(HttpClient client, WebApplicationFactory<Program> factory)
    {
        Client = client;
        Factory = factory;
    }

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();
    }
}

// A fixed-time double for IClock. Not NSubstitute (not an approved package for this project) —
// a plain hand-written fake is the simplest thing that satisfies the interface.
internal sealed class FixedClock : IClock
{
    public FixedClock(DateTimeOffset utcNow) => UtcNow = utcNow;
    public DateTimeOffset UtcNow { get; }
}
