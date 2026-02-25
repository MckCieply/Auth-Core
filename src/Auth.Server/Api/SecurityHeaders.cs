using Scalar.AspNetCore;

namespace Auth.Server.Api;

/// <summary>
/// The headers of spec 0008 → Security headers, on every response of the pipeline whatever its status (so also the framework's
/// <c>404</c> and <c>405</c>), set when the response starts. This replaces the no-store middleware of spec 0005. The interactive
/// reference (Development only) gets a policy of its own with the nonce of its one inline script. No
/// <c>Strict-Transport-Security</c>: TLS ends at the proxy, which sends it. The key set and the OpenAPI document send no
/// <c>Cache-Control</c> and get none.
/// </summary>
public static class SecurityHeaders
{
    public const string ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    private static readonly string OpenApiDocumentPath = $"/auth/openapi/{OpenApiSetup.DocumentName}.json";

    /// <summary>The policy for everything under the interactive reference. Without a nonce the script is not allowed to run.</summary>
    public static string ScalarPolicy(string? nonce) =>
        "default-src 'none'; script-src 'self'" + (nonce is null ? "" : $" 'nonce-{nonce}'")
        + "; style-src 'self' 'unsafe-inline'; img-src 'self'; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // The interactive reference exists in Development only: elsewhere its path is a 404 like any other, with the strict policy.
        var development = app.ApplicationServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment();
        return app.Use((context, next) =>
        {
            context.Response.OnStarting(static state => Apply(((HttpContext Context, bool Development))state), (context, development));
            return next(context);
        });
    }

    private static Task Apply((HttpContext Context, bool Development) state)
    {
        var (context, development) = state;
        var headers = context.Response.Headers;
        var path = context.Request.Path;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers["Content-Security-Policy"] = development && path.StartsWithSegments(OpenApiSetup.ReferencePath, StringComparison.OrdinalIgnoreCase)
            ? ScalarPolicy(context.Items.TryGetValue(ScalarOptions.NonceHttpContextItemKey, out var nonce) ? nonce as string : null)
            : ContentSecurityPolicy;

        var cacheable = path.Equals(OpenApiSetup.JwksPath, StringComparison.OrdinalIgnoreCase)
            || path.Equals(OpenApiDocumentPath, StringComparison.OrdinalIgnoreCase);
        if (!cacheable)
        {
            headers["Cache-Control"] = "no-store";
            headers["Pragma"] = "no-cache";
        }

        return Task.CompletedTask;
    }
}
