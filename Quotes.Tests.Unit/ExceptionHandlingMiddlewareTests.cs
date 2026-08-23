using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using QuotesApi.Middleware;

namespace Quotes.Tests.Unit;

/// <summary>
/// The last line of defence: whatever escapes the application becomes a
/// ProblemDetails response, not a stack trace and not a blank 500.
/// </summary>
/// <remarks>
/// Untested until now, and the reason is worth naming. The integration suite
/// never reached it because nothing in a healthy request throws. The only way to
/// exercise a global exception handler is to hand it something that fails, which
/// a unit test with a hostile RequestDelegate does in microseconds and an
/// end-to-end test cannot do at all without adding an endpoint whose only job is
/// to blow up.
/// </remarks>
public class ExceptionHandlingMiddlewareTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static (DefaultHttpContext Context, MemoryStream Body) NewContext(
        string path = "/api/quotes", string method = "GET")
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        context.Request.Path = path;
        context.Request.Method = method;
        return (context, body);
    }

    private static ExceptionHandlingMiddleware Middleware(
        RequestDelegate next, ILogger<ExceptionHandlingMiddleware>? logger = null)
        => new(next, logger ?? new RecordingLogger<ExceptionHandlingMiddleware>());

    private static async Task<ProblemDetails?> ReadProblem(MemoryStream body)
    {
        body.Position = 0;
        return await JsonSerializer.DeserializeAsync<ProblemDetails>(body, Json);
    }

    [Fact]
    public async Task InvokeAsync_WhenTheRequestSucceeds_WritesNothingOfItsOwn()
    {
        var (context, body) = NewContext();
        var middleware = Middleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        body.Length.Should().Be(0);
    }

    [Fact]
    public async Task InvokeAsync_WhenTheRequestThrows_Returns500ProblemDetails()
    {
        var (context, body) = NewContext("/api/quotes/7", "DELETE");
        var middleware = Middleware(_ => throw new InvalidOperationException("boom"));

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);

        var problem = await ReadProblem(body);
        problem.Should().NotBeNull();
        problem!.Status.Should().Be(StatusCodes.Status500InternalServerError);
        problem.Title.Should().Be("An unexpected error occurred.");
        problem.Instance.Should().Be("/api/quotes/7");
    }

    /// <summary>
    /// The bug this test found. The middleware set
    /// <c>Response.ContentType = "application/problem+json"</c> and then called
    /// <c>WriteAsJsonAsync</c>, which assigns
    /// <c>"application/json; charset=utf-8"</c> unconditionally and silently
    /// overwrote it. Every error response went out advertising the wrong media
    /// type, and a client keying off <c>application/problem+json</c> would not
    /// have recognised it as a problem document.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WhenTheRequestThrows_AdvertisesTheProblemJsonMediaType()
    {
        var (context, _) = NewContext();
        var middleware = Middleware(_ => throw new InvalidOperationException("boom"));

        await middleware.InvokeAsync(context);

        context.Response.ContentType.Should().StartWith(ExceptionHandlingMiddleware.ProblemJsonContentType);
    }

    [Fact]
    public async Task InvokeAsync_WhenTheRequestThrows_DoesNotLeakTheExceptionToTheCaller()
    {
        var (context, body) = NewContext();
        const string secret = "Server=example-sql;Password=REDACTED-EXAMPLE";
        var middleware = Middleware(_ => throw new InvalidOperationException(secret));

        await middleware.InvokeAsync(context);

        body.Position = 0;
        var raw = await new StreamReader(body).ReadToEndAsync();

        // The response says "try again later" and nothing else. Connection
        // strings, table names and stack frames stay on the server.
        raw.Should().NotContain(secret);
        raw.Should().NotContain("InvalidOperationException");
        raw.Should().Contain("Please try again later.");
    }

    [Fact]
    public async Task InvokeAsync_WhenTheRequestThrows_LogsTheExceptionWithMethodAndPath()
    {
        var (context, _) = NewContext("/api/quotes", "POST");
        var logger = new RecordingLogger<ExceptionHandlingMiddleware>();
        var boom = new InvalidOperationException("boom");
        var middleware = Middleware(_ => throw boom, logger);

        await middleware.InvokeAsync(context);

        // Hiding the detail from the caller is only defensible because it is kept
        // here. If this ever stops holding, production loses the only record of
        // the failure and the 500 becomes unactionable.
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Error);
        entry.Exception.Should().BeSameAs(boom);
        entry.Message.Should().Contain("POST").And.Contain("/api/quotes");
    }

    [Fact]
    public async Task InvokeAsync_WhenTheClientDisconnects_WritesNoResponse()
    {
        var (context, body) = NewContext();
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();
        context.RequestAborted = aborted.Token;

        var middleware = Middleware(_ => throw new OperationCanceledException());

        await middleware.InvokeAsync(context);

        body.Length.Should().Be(0);
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task InvokeAsync_WhenTheClientDisconnects_LogsAtInformationNotError()
    {
        var (context, _) = NewContext();
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();
        context.RequestAborted = aborted.Token;

        var logger = new RecordingLogger<ExceptionHandlingMiddleware>();
        var middleware = Middleware(_ => throw new OperationCanceledException(), logger);

        await middleware.InvokeAsync(context);

        // A user closing a tab is not an error. Logging it as one trains everyone
        // to ignore the error log, which is how the real errors get missed.
        logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Information);
    }

    [Fact]
    public async Task InvokeAsync_WhenCancellationIsNotFromTheClient_StillReturns500()
    {
        var (context, body) = NewContext();
        // RequestAborted is deliberately NOT cancelled: this is an internal
        // timeout or a stray token, not a client hanging up.
        var middleware = Middleware(_ => throw new OperationCanceledException());

        await middleware.InvokeAsync(context);

        // The `when` filter on the catch is the whole difference between these
        // two cases. Drop it and a genuine internal cancellation returns an empty
        // 200 — a failure that looks like a success, which is the worst kind.
        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        body.Length.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(NullReferenceException))]
    [InlineData(typeof(TimeoutException))]
    public async Task InvokeAsync_WhateverTheExceptionType_TheCallerSeesTheSameShape(Type exceptionType)
    {
        var (context, body) = NewContext();
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;
        var middleware = Middleware(_ => throw exception);

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        var problem = await ReadProblem(body);
        problem!.Title.Should().Be("An unexpected error occurred.");
    }
}
