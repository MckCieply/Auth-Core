using Auth.Server.Requests;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;
using Microsoft.Net.Http.Headers;

namespace Auth.Server.Api;

/// <summary>
/// A request body declared <c>application/json</c> with a <c>charset</c> other than <c>utf-8</c> (any case) is
/// <c>415 {"error":"unsupported_media_type"}</c>: every reader of a JSON body in this service reads UTF-8 (spec 0008 → Fixes). It
/// covers <c>POST</c>, <c>PUT</c>, <c>PATCH</c> and <c>DELETE</c> under <c>/auth/</c> other than refresh and logout, which read no
/// body, and it runs before authentication so that it also covers login, which OpenIddict answers inside it. A missing charset means
/// UTF-8, as before; a content type that is not JSON is the handler's own <c>400</c>.
/// </summary>
public static class JsonCharsetGuard
{
    internal static bool Applies(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!(HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method)
            || HttpMethods.IsPatch(request.Method) || HttpMethods.IsDelete(request.Method)))
        {
            return false;
        }

        var path = request.Path;
        return path.StartsWithSegments("/auth", StringComparison.OrdinalIgnoreCase)
            && !RefreshRequestHandler.IsRefreshPath(path)
            && !path.Equals(LogoutEndpoint.LogoutPath, StringComparison.OrdinalIgnoreCase)
            && !path.Equals(LogoutEndpoint.LogoutPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsRefused(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var parsed)
        && JsonObjectBody.IsJson(contentType)
        && parsed.Charset.HasValue
        && !HeaderUtilities.RemoveQuotes(parsed.Charset).Equals("utf-8", StringComparison.OrdinalIgnoreCase);   // charset="utf-8" keeps its quotes in the parsed value

    public static IApplicationBuilder UseJsonCharsetGuard(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            if (Applies(context.Request) && IsRefused(context.Request.ContentType))
            {
                await ApiResults.Error(TenancyErrors.UnsupportedMediaType).ExecuteAsync(context);
                return;
            }

            await next(context);
        });
    }
}
