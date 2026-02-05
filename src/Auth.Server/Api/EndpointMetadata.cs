using Auth.Server.Tenancy;

namespace Auth.Server.Api;

/// <summary>The body of every error of the API: <c>{"error":"&lt;code&gt;"}</c>.</summary>
public sealed record ErrorBody(string Error);

/// <summary>The codes an endpoint answers with at a status, so that the OpenAPI description can name them.</summary>
public sealed record ErrorCodesMetadata(int Status, IReadOnlyList<string> Codes);

/// <summary>The JSON body an endpoint reads, for the OpenAPI description only.</summary>
public sealed record JsonRequestMetadata(Type Body);

public static class EndpointMetadata
{
    /// <summary>
    /// Documents the JSON body the endpoint reads. It is not <c>Accepts</c> on purpose: that would make the routing
    /// answer <c>415</c> to another content type, and the contract is <c>400 invalid_request</c>, said by the handler.
    /// </summary>
    public static RouteHandlerBuilder ReadsJson<T>(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithMetadata(new JsonRequestMetadata(typeof(T)));
    }

    /// <summary>Documents an error status of the endpoint and the codes it carries.</summary>
    public static RouteHandlerBuilder ProducesError(this RouteHandlerBuilder builder, int status, params string[] codes)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Produces<ErrorBody>(status, "application/json").WithMetadata(new ErrorCodesMetadata(status, codes));
    }

    /// <summary>
    /// The answers every endpoint behind an access token and a permission check can give: a <c>401</c> with an empty
    /// body and a <c>WWW-Authenticate: Bearer</c> challenge, and the two <c>403</c>s of the permission check.
    /// </summary>
    public static RouteHandlerBuilder ProducesGuarded(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionsChanged, TenancyErrors.Forbidden);
    }
}
