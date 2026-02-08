using Microsoft.Net.Http.Headers;

namespace Auth.Server.Api;

/// <summary>
/// Marks every response under the paths of spec 0005 as never to be stored, including the ones no handler of ours
/// writes: the <c>401</c> of a missing or invalid token, a <c>404</c>, a <c>405</c>. It runs before authentication
/// for that reason; the handlers' own results set the same headers.
/// </summary>
public static class NoStoreMiddleware
{
    private static readonly string[] Prefixes = ["/auth/me", "/auth/org", "/auth/invites"];

    public static IApplicationBuilder UseNoStoreForTenancyPaths(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            if (Prefixes.Any(prefix => context.Request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers[HeaderNames.CacheControl] = "no-store";
                    context.Response.Headers[HeaderNames.Pragma] = "no-cache";
                    return Task.CompletedTask;
                });
            }

            await next(context);
        });
    }
}
