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
        catch (Exception ex)
        {
            // The caller is told nothing useful, so the detail has to survive
            // here or it is gone.
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
