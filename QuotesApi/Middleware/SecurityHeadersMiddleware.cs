namespace QuotesApi.Middleware;

/// <summary>
/// Day 27. The response headers a JSON API should always send, and the one it
/// should only send over HTTPS.
/// </summary>
/// <remarks>
/// These are cheap, and every one of them is a ZAP baseline finding when it is
/// missing. They are written here rather than at the ingress because the
/// ingress is not the only way this app is reached - it also runs on a laptop
/// and inside WebApplicationFactory, and a control that only exists in
/// production is a control nobody ever tests.
///
/// Two choices worth defending:
///
/// <b>The CSP is `default-src 'none'`.</b> A stricter policy than a web app
/// could use, and correct here precisely because this is an API: it returns
/// JSON, never HTML, so there is no legitimate script, style, image or frame
/// to allow. If a response ever does get rendered in a browser - an error page,
/// a mis-typed content type, a reflected value - this policy means nothing in
/// it executes. `frame-ancestors 'none'` is the modern half of X-Frame-Options
/// and is repeated there for older browsers.
///
/// <b>HSTS is conditional.</b> Sending Strict-Transport-Security over plain
/// HTTP is meaningless - the spec says a browser must ignore it - and locally
/// this app is plain HTTP, so sending it unconditionally would be noise in dev
/// and would also pin localhost to HTTPS in any browser that did honour it,
/// which is a genuinely annoying thing to do to a developer. Container Apps
/// terminates TLS at its ingress and forwards X-Forwarded-Proto, which
/// UseForwardedHeaders turns back into Request.IsHttps - so this fires in
/// Azure and stays quiet on a laptop.
/// </remarks>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        // Content sniffing turns a JSON response the browser was told not to
        // render into one it renders anyway.
        headers["X-Content-Type-Options"] = "nosniff";

        // No part of this API is meant to be framed.
        headers["X-Frame-Options"] = "DENY";
        headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";

        // Do not leak the requested URL - which can carry ids - to whatever the
        // user navigates to next.
        headers["Referrer-Policy"] = "no-referrer";

        // Keep responses out of another origin's document.
        headers["Cross-Origin-Resource-Policy"] = "same-origin";

        if (context.Request.IsHttps)
        {
            headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        }

        return next(context);
    }
}

public static class SecurityHeadersMiddlewareExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
        => app.UseMiddleware<SecurityHeadersMiddleware>();
}
