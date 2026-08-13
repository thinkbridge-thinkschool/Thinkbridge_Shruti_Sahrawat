using Serilog.Context;

namespace QuotesApi.Middleware;

public static class CorrelationIdMiddlewareExtensions
{
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
    {
        return app.Use(async (ctx, next) =>
        {
            using (LogContext.PushProperty("TraceId", ctx.TraceIdentifier))
            {
                await next();
            }
        });
    }
}