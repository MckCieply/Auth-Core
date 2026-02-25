using Auth.Server.Audit;
using Auth.Server.Tenancy;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Sessions;

/// <summary>
/// Records <c>refresh.reuse_detected</c> (spec 0008 → Audit log). OpenIddict decides a reuse in <c>ValidateTokenEntry</c>, where a
/// rejection ends the handlers after it, so this handler runs just before and asks the same question: the entry is a refresh
/// token, it has been redeemed, and the redemption is older than the reuse leeway. Inside the leeway an honest retry is let
/// through and nothing is recorded; once OpenIddict has revoked the family the status is <c>revoked</c>, so a repeated replay does
/// not record again. It changes nothing about the answer: once the token entry has been read, a failure to look up the account's company
/// or to write the row is logged at <c>Warning</c> and swallowed, so that OpenIddict still decides the reuse and revokes the family.
/// </summary>
public sealed partial class RefreshReuseAuditHandler(
    IOpenIddictTokenManager tokens, TimeProvider clock, AuditLog audit, MembershipReader memberships, ILogger<RefreshReuseAuditHandler> logger)
    : IOpenIddictServerHandler<OpenIddictServerEvents.ValidateTokenContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ValidateTokenContext>()
            .UseScopedHandler<RefreshReuseAuditHandler>()
            .SetOrder(OpenIddictServerHandlers.Protection.ValidateTokenEntry.Descriptor.Order - 1)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    /// <inheritdoc />
    public async ValueTask HandleAsync(OpenIddictServerEvents.ValidateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrEmpty(context.TokenId))
        {
            return;
        }

        var token = await tokens.FindByIdAsync(context.TokenId, context.CancellationToken);
        if (token is null
            || !await tokens.HasTypeAsync(token, TokenTypeIdentifiers.RefreshToken, context.CancellationToken)
            || !await tokens.HasStatusAsync(token, Statuses.Redeemed, context.CancellationToken)
            || await tokens.GetRedemptionDateAsync(token, context.CancellationToken) is not { } redeemedAt
            || redeemedAt + SessionPolicy.ReuseLeeway > clock.GetUtcNow())
        {
            return;
        }

        try
        {
            var subject = Guid.TryParse(await tokens.GetSubjectAsync(token, context.CancellationToken), out var userId) ? userId : (Guid?)null;
            var tenant = subject is { } id ? await memberships.ReadAsync(id, context.CancellationToken) : null;
            await audit.WriteAloneAsync(new AuditEntry
            {
                Kind = AuditKinds.RefreshReuseDetected,
                SubjectUserId = subject,
                OrgId = tenant?.CompanyId,
                OrgName = tenant?.CompanyName,
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !context.CancellationToken.IsCancellationRequested)
        {
            // No row: a handler that throws here would stop ValidateTokenEntry, and the replay would get a 500 and keep its family.
            LogNotRecorded(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A reused refresh token could not be recorded in the audit log; the refresh is answered as usual.")]
    private static partial void LogNotRecorded(ILogger logger, Exception exception);
}
