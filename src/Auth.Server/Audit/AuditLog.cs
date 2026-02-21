using System.Text.Json;
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Auth.Server.Network;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Audit;

/// <summary>
/// The writer of the audit log (spec 0008 → Audit log), scoped like the <c>DbContext</c> it uses. <see cref="Stage"/> is for a
/// change: the row goes onto the request's context and is written by the save, inside the transaction, that commits the change,
/// so that either both are there or neither. <see cref="WriteAloneAsync"/> is for an event that changes nothing else: it saves at
/// once, from a scope and a context of its own (it must never save what the request's context is tracking, and a failure must
/// leave nothing behind there), and never fails the request it describes. Neither ever takes a password, a token, a link, a cookie or a mail body: the
/// callers hand over names, reasons and counts only.
/// </summary>
public sealed partial class AuditLog(
    AuthDbContext db, IServiceScopeFactory scopes, IHttpContextAccessor http, TimeProvider clock, ILogger<AuditLog> logger)
{
    public const int MaxEmailLength = 256;
    public const int MaxOrgNameLength = 100;

    /// <summary>Adds the row to the context; the caller's own <c>SaveChangesAsync</c> writes it.</summary>
    public AuditEvent Stage(AuditEntry entry, Actor? actor = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var row = Build(entry, actor);
        db.AuditEvents.Add(row);
        return row;
    }

    /// <summary>
    /// Writes the row now, from a scope and a context of its own: the request's context may be tracking a change that is half made,
    /// and this save must neither write it nor be undone by it. A failure is logged and swallowed, and nothing retries the row.
    /// </summary>
    public async Task WriteAloneAsync(AuditEntry entry, Actor? actor = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var row = Build(entry, actor);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var own = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            own.AuditEvents.Add(row);
            await own.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogNotWritten(logger, entry.Kind, exception);
        }
    }

    private AuditEvent Build(AuditEntry entry, Actor? actor)
    {
        var details = entry.Details is { Count: > 0 } given ? new Dictionary<string, object?>(given) : [];
        if (actor is { IsOperator: true })
        {
            details["via"] = "cli";
        }

        return new AuditEvent
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = StorableTime.Now(clock),
            Kind = entry.Kind,
            ActorUserId = entry.ActorUserId ?? actor?.UserId,
            SubjectUserId = entry.SubjectUserId,
            SubjectEmail = Truncate(entry.SubjectEmail, MaxEmailLength),
            OrgId = entry.OrgId,
            OrgName = Truncate(entry.OrgName, MaxOrgNameLength),
            TargetId = entry.TargetId,
            ClientIp = http.HttpContext is { } context ? ClientAddress.Text(context) : null,
            Details = details.Count == 0 ? null : JsonSerializer.Serialize(details),
        };
    }

    /// <summary>At most <paramref name="max"/> characters, and never half of a surrogate pair.</summary>
    internal static string? Truncate(string? value, int max)
    {
        if (value is null || value.Length <= max)
        {
            return value;
        }

        return value[..(char.IsHighSurrogate(value[max - 1]) ? max - 1 : max)];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The audit event {Kind} could not be written.")]
    private static partial void LogNotWritten(ILogger logger, string kind, Exception exception);
}
