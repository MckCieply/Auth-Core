using System.Security.Cryptography;
using System.Text;
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Tenancy;

/// <summary>
/// The link tokens of invitations (spec 0005 → Invitations). They work like the link tokens of spec 0004 — 32 random
/// bytes as base64url, only the SHA-256 stored, single use — but live on the invitation, because an invitation names an
/// address that may have no account, and <see cref="EmailToken"/> rows belong to users.
/// </summary>
public static class InviteTokens
{
    /// <summary>An invitation is valid this long from the moment its mail is composed. A constant, as the lifetimes of spec 0004.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// Gives the invitation a new token, replacing the one it had, and returns it in clear; the caller puts it into a mail
    /// and nowhere else. The seven days start now. The invitation row must be locked by the caller, as the dispatcher does.
    /// </summary>
    public static async Task<string> IssueAsync(AuthDbContext db, Guid inviteId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var token = EmailTokens.NewToken();
        var hash = EmailTokens.HashOf(token);
        var expiresAt = now + Lifetime;
        await db.Database.ExecuteSqlAsync(
            $"""UPDATE "Invites" SET "TokenHash" = {hash}, "ExpiresAt" = {expiresAt} WHERE "Id" = {inviteId}""",
            cancellationToken);
        return token;
    }

    /// <summary>
    /// The identifier the mail limit counts an invitation under: the company and the address together, so that one
    /// company's invitations to a person neither block nor reveal another's (spec 0005, Decision 16). Domain-separated,
    /// so that it can never equal the hash the per-address limits of spec 0004 use for the same address.
    /// </summary>
    public static byte[] LimitIdentifierOf(Guid companyId, string normalizedEmail)
    {
        ArgumentNullException.ThrowIfNull(normalizedEmail);

        return SHA256.HashData(Encoding.UTF8.GetBytes($"invitation\0{companyId:N}\0{normalizedEmail}"));
    }
}
