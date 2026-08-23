using System.Runtime.CompilerServices;

namespace Quotes.Tests.Integration;

/// <summary>
/// Clears telemetry destinations out of the environment before any test host starts.
/// </summary>
/// <remarks>
/// The same timing trap that broke the JWT key in OrderRefactor.Tests applies
/// here, in the other direction.
///
/// <c>QuotesApi/Program.cs</c> reads both of these while the builder is still
/// being configured:
///
/// <code>
///     builder.Configuration["Otel:OtlpEndpoint"] ?? builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]
///     builder.Configuration["ApplicationInsights:ConnectionString"] ?? builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]
/// </code>
///
/// Nulling those keys through <c>ConfigureAppConfiguration</c> in the test host
/// looks like it disables the exporters and does nothing at all: those delegates
/// are replayed when the host is built, long after the entry point read the
/// values. Running the tests as <c>Testing</c> rather than <c>Development</c>
/// keeps <c>appsettings.Development.json</c> out of the picture, which is why
/// the suite is fast — but an <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> or an
/// <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> sitting in a developer's shell
/// or on a build agent would still be picked up, and would put the 41-minute
/// export timeouts straight back.
///
/// Environment variables are read at <c>CreateBuilder</c>, so clearing them here
/// lands before that line runs. A module initializer executes before any test in
/// the assembly, so nothing has to remember to call it.
///
/// The stakes are not only speed. A connection string left in the environment
/// would have this suite posting telemetry from hundreds of throwaway databases
/// into a real Application Insights resource.
/// </remarks>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void SilenceTelemetryExporters()
    {
        Environment.SetEnvironmentVariable("Otel__OtlpEndpoint", null);
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", null);
        Environment.SetEnvironmentVariable("ApplicationInsights__ConnectionString", null);
        Environment.SetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING", null);
    }
}
