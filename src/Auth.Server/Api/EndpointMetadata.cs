using Auth.Server.Tenancy;

namespace Auth.Server.Api;

/// <summary>The body of every error of the API: <c>{"error":"&lt;code&gt;"}</c>.</summary>
public sealed record ErrorBody(string Error);

/// <summary>The <c>400 weak_password</c> body: the password policy rules (spec 0004) the password breaks.</summary>
public sealed record WeakPasswordBody(string Error, IReadOnlyList<string> Rules);

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
    /// The <c>400</c> of an endpoint that sets a password: <c>invalid_request</c> and <c>invalid_token</c> as everywhere, and
    /// <c>weak_password</c>, whose body also names the rules the password breaks.
    /// </summary>
    public static RouteHandlerBuilder ProducesPasswordError(this RouteHandlerBuilder builder, params string[] codes)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .Produces<WeakPasswordBody>(StatusCodes.Status400BadRequest, "application/json")
            .WithMetadata(new ErrorCodesMetadata(StatusCodes.Status400BadRequest, codes));
    }

    /// <summary>The <c>429</c> of the lockout or of a mail limit: the lockout body, and the <c>Retry-After</c> header with the same number.</summary>
    public static RouteHandlerBuilder ProducesTooManyAttempts(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .Produces<TooManyAttemptsBody>(StatusCodes.Status429TooManyRequests, "application/json")
            .WithMetadata(new ErrorCodesMetadata(StatusCodes.Status429TooManyRequests, [TenancyErrors.TooManyAttempts]));
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
