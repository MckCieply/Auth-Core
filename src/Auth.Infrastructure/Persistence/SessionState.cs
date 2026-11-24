namespace Auth.Infrastructure.Persistence;

/// <summary>
/// What a session records beside its OpenIddict authorization (spec 0012 → Sessions, Amendment A8): when it began, which the 30-day cap counts
/// from and a renewal keeps, and until when it is confirmed (the confirmation window; <see langword="null"/>: closed). The row is keyed by the
/// authorization's id and goes with it (<c>ON DELETE CASCADE</c>): a pruned or deleted authorization takes its values along.
/// </summary>
public sealed class SessionState
{
    public required string AuthorizationId { get; init; }

    public required DateTimeOffset SessionStart { get; init; }

    public DateTimeOffset? ConfirmedUntil { get; set; }
}
