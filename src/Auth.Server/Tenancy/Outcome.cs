namespace Auth.Server.Tenancy;

/// <summary>The error codes of spec 0005 → Contract, and the status each one is answered with.</summary>
public static class TenancyErrors
{
    public const string InvalidRequest = "invalid_request";
    public const string InvalidToken = "invalid_token";
    public const string WeakPassword = "weak_password";
    public const string UnknownPermission = "unknown_permission";
    public const string Forbidden = "forbidden";
    public const string PermissionsChanged = "permissions_changed";
    public const string PermissionNotHeld = "permission_not_held";
    public const string NotFound = "not_found";
    public const string AlreadyInOrg = "already_in_org";
    public const string AlreadyMember = "already_member";
    public const string InvitePending = "invite_pending";
    public const string LastManager = "last_manager";
    public const string CannotChangeSelf = "cannot_change_self";
    public const string RoleInUse = "role_in_use";
    public const string RoleNameTaken = "role_name_taken";
    public const string TooManyAttempts = "too_many_attempts";
    public const string TooManyRequests = "too_many_requests";

    public static int StatusOf(string error) => error switch
    {
        InvalidRequest or InvalidToken or WeakPassword or UnknownPermission => StatusCodes.Status400BadRequest,
        Forbidden or PermissionsChanged or PermissionNotHeld => StatusCodes.Status403Forbidden,
        NotFound => StatusCodes.Status404NotFound,
        AlreadyInOrg or AlreadyMember or InvitePending or LastManager or CannotChangeSelf or RoleInUse or RoleNameTaken
            => StatusCodes.Status409Conflict,
        TooManyAttempts or TooManyRequests => StatusCodes.Status429TooManyRequests,
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, "Unknown error code."),
    };
}

/// <summary>
/// What a service did: nothing wrong, or the code of what was refused. The API turns the code into a response and the
/// operator commands print it, so a refusal means the same on both.
/// </summary>
public readonly record struct Outcome(string? Error = null, TimeSpan RetryAfter = default)
{
    public static Outcome Done => default;

    public bool Succeeded => Error is null;

    public static Outcome Fail(string error) => new(error);

    /// <summary>Over the mail limit: refused, and when it would not be.</summary>
    public static Outcome Limited(TimeSpan retryAfter) => new(TenancyErrors.TooManyAttempts, retryAfter);

    public static Outcome<T> Ok<T>(T value) => new(value);

    public static Outcome<T> Fail<T>(string error) => new(default, error);
}

/// <summary>An <see cref="Outcome"/> that carries a value when it succeeded.</summary>
public readonly record struct Outcome<T>(T? Value, string? Error = null, TimeSpan RetryAfter = default)
{
    public bool Succeeded => Error is null;

    public Outcome Without => new(Error, RetryAfter);
}
