namespace Auth.Server.Sessions;

/// <summary>
/// The two paths that renew a session with the refresh cookie (spec 0015): <c>POST /auth/refresh</c> and <c>POST /auth/switch</c>. OpenIddict
/// matches a token-endpoint path with an optional trailing slash, so every check here agrees with it.
/// </summary>
public static class SessionPaths
{
    /// <summary>Switches the session to another space of the account.</summary>
    public const string Switch = "/auth/switch";

    /// <summary><see langword="true"/> for <c>/auth/switch</c> and <c>/auth/switch/</c>, case-insensitive, and nothing else.</summary>
    internal static bool IsSwitchPath(PathString path) =>
        path.Equals(Switch, StringComparison.OrdinalIgnoreCase)
        || path.Equals(Switch + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <see langword="true"/> for a refresh or a switch: the paths whose answers <see cref="SessionResponseHandler"/> writes (the uniform
    /// <c>401</c>, the compact body, the <c>503</c>) and whose outage <c>ErrorHandlingMiddleware</c> answers as a <c>503</c>. Not the predicate of
    /// <c>JsonCharsetGuard</c>: a switch reads a body, so it stays under the guard.
    /// </summary>
    internal static bool IsSessionPath(PathString path) =>
        RefreshRequestHandler.IsRefreshPath(path) || IsSwitchPath(path);
}
