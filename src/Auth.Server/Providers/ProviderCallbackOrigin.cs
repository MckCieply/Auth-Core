namespace Auth.Server.Providers;

/// <summary>
/// OpenIddict compares the URL of a callback request with the callback address it stored in the state, and refuses any difference of scheme, host, port or path.
/// Behind a proxy that does not keep the browser's scheme and host, every callback would fail; and the address must never come from the request
/// (Settled 2). So for the two callback paths only, before authentication, the scheme and the host of the request are those of the return
/// address's origin (Amendment A2). Nothing else of the request changes, and no other path is touched.
/// </summary>
public static class ProviderCallbackOrigin
{
    public static IApplicationBuilder UseProviderCallbackOrigin(this IApplicationBuilder app, ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.ReturnOrigin is not { } origin)
        {
            return app;   // no provider is offered, and no round trip ever starts
        }

        var host = HostString.FromUriComponent(origin);
        return app.Use((context, next) =>
        {
            if (ProviderPaths.IsCallbackPath(context.Request.Path))
            {
                context.Request.Scheme = origin.Scheme;
                context.Request.Host = host;
            }

            return next(context);
        });
    }
}
