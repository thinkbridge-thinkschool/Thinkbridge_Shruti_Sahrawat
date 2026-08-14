using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Quotes.Tests.Unit;

public class ResilienceHandlerTests
{
    [Fact]
    public async Task RetryHandler_RecoversAfterTwoTransientFailures()
    {
        var handler = new SequenceHandler(
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.OK);

        var services = new ServiceCollection();
        services.AddHttpClient("my-service")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddResilienceHandler("test", b => b.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Constant,
                Delay = TimeSpan.Zero,
                UseJitter = false
            }));

        var factory = services.BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>();

        var response = await factory.CreateClient("my-service")
            .GetAsync("http://stub.invalid/resource");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.Attempts.Should().Be(3, "two 503s should be retried before the 200 succeeds");
    }

    [Fact]
    public async Task RetryHandler_SurfacesFailure_WhenAllAttemptsFail()
    {
        var handler = new SequenceHandler(HttpStatusCode.ServiceUnavailable);

        var services = new ServiceCollection();
        services.AddHttpClient("my-service")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddResilienceHandler("test", b => b.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Constant,
                Delay = TimeSpan.Zero,
                UseJitter = false
            }));

        var factory = services.BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>();

        var response = await factory.CreateClient("my-service")
            .GetAsync("http://stub.invalid/resource");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        handler.Attempts.Should().Be(4, "1 initial attempt plus 3 retries");
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode[] _responses;

        public SequenceHandler(params HttpStatusCode[] responses) => _responses = responses;

        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var status = _responses[Math.Min(Attempts, _responses.Length - 1)];
            Attempts++;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
