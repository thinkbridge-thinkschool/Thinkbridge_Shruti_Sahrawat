using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using QuotesApi.Configuration;
using QuotesApi.Data;
using QuotesApi.Models;
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
// The host applies them at startup because CreateFreshHost sets
// Database:SchemaBootstrap to Migrate — see that setting below.
internal static class TestInfrastructure
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A fixed signing key for the test host.
    /// </summary>
    /// <remarks>
    /// Program.cs generates a random one per process outside Production, which
    /// is right for a developer machine and wrong here: it would mean a token
    /// minted in one test host cannot be read by another, and any future test
    /// that wants to hand-craft a token has nothing stable to sign it with.
    /// Long enough to satisfy the 32-byte HMAC-SHA256 minimum. It authenticates
    /// nothing outside this test process.
    /// </remarks>
    public const string TestSigningKey = "integration-tests-only-signing-key-not-a-secret";

    /// <summary>The password every account created by these helpers uses.</summary>
    public const string TestPassword = "correct-horse-battery";

    public static async Task<TestHost> CreateFreshHost(
        MsSqlContainerFixture sqlServer,
        IClock? clock = null,
        params string[] adminEmails)
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
                var settings = new Dictionary<string, string?>
                {
                    ["Serilog:MinimumLevel:Default"] = "Warning",
                    ["Serilog:MinimumLevel:Override:Microsoft"] = "Warning",
                    ["Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore"] = "Warning",
                    ["Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore.Database.Command"] = "Warning",
                    ["Jwt:Key"] = TestSigningKey,

                    // The host must apply the SQL-Server migrations below, not
                    // EnsureCreated() the schema from the model.
                    //
                    // Program.cs defaults SQL Server to EnsureCreated(), which
                    // is correct for Azure SQL - no SQL-Server migration set
                    // ships in the deployed image - and is a no-op here,
                    // because CreateFreshHost has already issued CREATE
                    // DATABASE. An existing database means EnsureCreated()
                    // returns false and creates nothing, so every test would
                    // run against an empty schema and fail registering its
                    // first user.
                    //
                    // Unlike Jwt:Key above, this one does take effect:
                    // Program.cs reads it from the *built* app's
                    // configuration, after these delegates have been replayed.
                    ["Database:SchemaBootstrap"] = "Migrate"
                };

                // Configuration arrays are flattened to indexed keys - the same
                // shape ASP.NET Core reads a JSON array as - so that a test can
                // declare which addresses register as admins.
                for (var i = 0; i < adminEmails.Length; i++)
                {
                    settings[$"Auth:AdminEmails:{i}"] = adminEmails[i];
                }

                config.AddInMemoryCollection(settings);
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

                // Auth tokens are minted against the real clock, always - never
                // against whatever domain IClock a test just installed above.
                //
                // JwtBearer's ValidateLifetime compares a token's exp against
                // actual DateTime.UtcNow; it has no idea this app's IClock was
                // overridden. CreateFreshHost signs in a default user via a real
                // HTTP call before handing the host back (see SignInDefaultUser
                // below), and that call goes through JwtTokenService like any
                // other. Without this override, a test that moves the domain
                // clock to answer "what does CreatedAt look like on 20 July
                // 1969" would also mint that test's own sign-in token dated
                // 1969 - already expired the instant it is issued, so every
                // request the test makes with it 401s before its actual
                // assertion ever runs.
                services.RemoveAll<ITokenService>();
                services.AddScoped<ITokenService>(sp => new JwtTokenService(
                    sp.GetRequiredService<IOptions<JwtOptions>>(),
                    new SystemClock()));
            });
        });

        var host = new TestHost(factory.CreateClient(), factory);

        // Every quotes endpoint requires a token as of Day 19, so a test host
        // whose Client could not call them would be useless to almost every
        // test in this project. Client is therefore signed in as an ordinary
        // user from the moment it is handed over, and the tests that care
        // about the *absence* of a token ask for AnonymousClient() explicitly.
        //
        // The alternative - making all thirteen existing tests register a user
        // as their first Arrange line - would have added the same three lines
        // to every one of them to say something none of them are about.
        await host.SignInDefaultUser();
        return host;
    }
}

internal sealed class TestHost : IDisposable
{
    /// <summary>The default email <see cref="Client"/> is signed in as.</summary>
    public const string DefaultUserEmail = "default-user@example.com";

    private readonly List<HttpClient> _extraClients = new();

    /// <summary>Signed in as <see cref="DefaultUserEmail"/>, an ordinary user.</summary>
    public HttpClient Client { get; }

    public WebApplicationFactory<Program> Factory { get; }

    /// <summary>The id of the account <see cref="Client"/> is signed in as.</summary>
    public int DefaultUserId { get; private set; }

    public TestHost(HttpClient client, WebApplicationFactory<Program> factory)
    {
        Client = client;
        Factory = factory;
    }

    /// <summary>A client carrying no token at all.</summary>
    public HttpClient AnonymousClient()
    {
        var client = Factory.CreateClient();
        _extraClients.Add(client);
        return client;
    }

    /// <summary>
    /// Registers a second account and returns a client signed in as it.
    /// </summary>
    /// <remarks>
    /// A separate HttpClient rather than swapping the header on the shared one:
    /// a test proving that one user cannot see another's quotes needs both
    /// identities usable at once, and a single client whose Authorization
    /// header is reassigned between calls makes the order of those calls
    /// load-bearing in a way that is easy to get subtly wrong.
    ///
    /// To register as an admin, pass that address to CreateFreshHost's
    /// adminEmails - the role is decided by the server from configuration, and
    /// there is deliberately no way for a caller to ask for one.
    /// </remarks>
    public async Task<(HttpClient Client, UserResponse User)> SignUpAsync(string email)
    {
        var client = Factory.CreateClient();
        _extraClients.Add(client);
        var user = await Authenticate(client, email);
        return (client, user);
    }

    internal async Task SignInDefaultUser()
    {
        var user = await Authenticate(Client, DefaultUserEmail);
        DefaultUserId = user.Id;
    }

    private static async Task<UserResponse> Authenticate(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new { email, password = TestInfrastructure.TestPassword });

        // Loud, and early. A registration that quietly failed would surface
        // later as an unrelated 401 in whatever the test actually asserts,
        // sending whoever debugs it looking at the wrong endpoint.
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Test setup could not register {email}: {(int)response.StatusCode} {response.StatusCode}. {body}");
        }

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(TestInfrastructure.Json)
                   ?? throw new InvalidOperationException("Register returned an empty body.");

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        return auth.User;
    }

    public void Dispose()
    {
        foreach (var client in _extraClients)
        {
            client.Dispose();
        }

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
