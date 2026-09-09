using Microsoft.AspNetCore.Mvc;

namespace QuotesApi.Middleware;

public class ExceptionHandlingMiddleware
{
    /// <summary>RFC 7807 media type for a problem document.</summary>
    public const string ProblemJsonContentType = "application/problem+json";

    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client hung up. Nobody is listening, and writing to a closed
            // socket would only raise a second exception inside the handler.
            //
            // The `when` filter matters: without it, a genuine internal
            // cancellation (a timeout, a stray token) would also be swallowed
            // and returned as an empty 200. See
            // ExceptionHandlingMiddlewareTests.InvokeAsync_WhenCancellationIsNotFromTheClient_StillReturns500.
            _logger.LogInformation(
                "Request aborted by the client: {Method} {Path}",
                context.Request.Method, context.Request.Path);
        }
        catch (BadHttpRequestException ex)
        {
            // Day 26. A malformed request is the caller's fault, and saying so
            // is not pedantry about status codes.
            //
            // ASP.NET Core throws this when model binding cannot satisfy a
            // handler's parameters - a missing required query string, an int
            // that will not parse. Caught by the general handler below it
            // became a 500, which is the server claiming responsibility for a
            // mistake the client made, and it has a measurable cost: 22
            // requests missing a `size` parameter reported as a 100% *server*
            // error rate on GET /api/quotes, which is exactly the signal Day
            // 26's error-rate alert is built on. An alert that fires because
            // somebody typo'd a query string is an alert people learn to
            // ignore.
            //
            // Found by sending malformed requests by accident while generating
            // load, which is a fair approximation of how real clients find it.
            var activity = System.Diagnostics.Activity.Current;
            activity?.AddException(ex);

            // Deliberately not SetStatus(Error). The span records what happened
            // for whoever is debugging, but a client sending a bad request is
            // not this service failing, and marking it so would put it back
            // into the same bucket the status code just moved it out of.

            // Warning, not Error: worth seeing in aggregate if one endpoint
            // suddenly starts rejecting everything, not worth paging anyone.
            _logger.LogWarning(ex, "Bad request on {Method} {Path}",
                context.Request.Method, context.Request.Path);

            context.Response.StatusCode = StatusCodes.Status400BadRequest;

            var badRequest = new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Bad request.",
                // The message *is* told to the caller here, unlike the 500 path
                // below. It describes the shape of their own request - which
                // parameter is missing - and leaks nothing about this server.
                // Withholding it would leave them guessing at something only
                // they can fix.
                Detail = ex.Message,
                Instance = context.Request.Path
            };

            await context.Response.WriteAsJsonAsync(
                badRequest, options: null, contentType: ProblemJsonContentType);
        }
        catch (Exception ex)
        {
            // Day 26. Attach the exception to the current span before doing
            // anything else with it.
            //
            // This middleware is the reason nothing above it ever sees an
            // unhandled exception - which is the point, and which also means
            // ASP.NET Core's telemetry integration never sees one either.
            // Before this line, App Insights recorded these requests as
            // success == false with resultCode 500 and no `exceptions` row at
            // all: `exceptions | where timestamp > ago(30m)` returned an empty
            // set while 22 of 22 GETs were failing. You could see *that* the
            // endpoint was broken and had to go and find the one console that
            // happened to serve the request to learn *why*.
            //
            // Found exactly that way (Days/day-26): a real 500 on
            // GET /api/quotes, invisible in telemetry, diagnosable only from a
            // terminal. Logging to Serilog was never the gap - the gap was
            // that the log and the trace were in different places, so the
            // trace could not answer the question the trace was for.
            var activity = System.Diagnostics.Activity.Current;
            activity?.AddException(ex);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);

            // Still logged, and still with the full exception. The span carries
            // it to App Insights; this carries it to whoever is watching stdout.
            // The caller is told nothing useful either way, so the detail has
            // to survive in both places or it is gone.
            _logger.LogError(ex, "Unhandled exception on {Method} {Path}",
                context.Request.Method, context.Request.Path);

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;

            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
                Detail = "Please try again later.",
                Instance = context.Request.Path
            };

            // The content type is passed to WriteAsJsonAsync rather than set on
            // the response beforehand. Setting Response.ContentType first looks
            // like it works and does not: WriteAsJsonAsync assigns
            // "application/json; charset=utf-8" unconditionally, overwriting it,
            // so the problem document went out advertising the wrong media type
            // and a client keying off application/problem+json would not have
            // recognised it. Caught by a test asserting the header.
            await context.Response.WriteAsJsonAsync(
                problem, options: null, contentType: ProblemJsonContentType);
        }
    }
}

public static class ExceptionHandlingMiddlewareExtensions
{
    public static IApplicationBuilder UseExceptionHandling(this IApplicationBuilder app)
        => app.UseMiddleware<ExceptionHandlingMiddleware>();
}
