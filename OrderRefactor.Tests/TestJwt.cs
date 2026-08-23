using System.Runtime.CompilerServices;

namespace OrderRefactor.Tests;

/// <summary>
/// Supplies the JWT signing key to every test host in this assembly.
/// </summary>
/// <remarks>
/// The key is delivered as an environment variable, and the reason is a timing
/// detail that is easy to get wrong twice.
///
/// <c>Program.cs</c> reads the key while the builder is still being configured,
/// because <c>AddJwtBearer</c> needs it to construct
/// <c>TokenValidationParameters</c>:
///
/// <code>
///     var jwtOptions = builder.Configuration.GetSection("Jwt").Get&lt;JwtOptions&gt;();
/// </code>
///
/// The obvious way to feed a test host its own settings —
/// <c>WithWebHostBuilder(b =&gt; b.ConfigureAppConfiguration(...))</c> — does not
/// reach that line. <c>WebApplicationFactory</c> collects those delegates in a
/// <c>DeferredHostBuilder</c> and replays them when the host is *built*, which
/// is after the entry point has already run and already read the value. The
/// failure is emphatic rather than subtle: every host-booting test throws
/// <c>Jwt:Key is missing</c> from <c>Program.cs</c>, while every pure unit test
/// in the same assembly passes.
///
/// Environment variables are read by <c>WebApplication.CreateBuilder</c> at
/// construction, so a value set here is in place before that line executes. A
/// module initializer runs before any test in the assembly, so no test class
/// has to remember to do it.
///
/// This is also the same channel production uses — a Key Vault reference
/// surfaced as the <c>Jwt__Key</c> environment variable — rather than a
/// mechanism that only exists inside the test host. Relying on
/// <c>dotnet user-secrets</c> instead would make the suite pass or fail
/// depending on what is on the machine running it, which is precisely what CI
/// exists to rule out.
/// </remarks>
internal static class TestJwt
{
    /// <summary>
    /// Test-only signing key. Not a secret, and deliberately not the one used
    /// anywhere else: if this value ever appeared in a real token, that token
    /// was minted by the test suite.
    /// </summary>
    public const string Key = "order-refactor-tests-signing-key-not-a-secret";

    [ModuleInitializer]
    internal static void SupplySigningKeyToEveryTestHost()
    {
        // Double underscore is the configuration hierarchy separator for
        // environment variables: Jwt__Key binds to Jwt:Key.
        Environment.SetEnvironmentVariable("Jwt__Key", Key);
    }
}
