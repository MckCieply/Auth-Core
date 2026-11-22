using Auth.Infrastructure.Persistence;
using Auth.Server.Requests;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Api;

/// <summary>
/// <c>POST /auth/orgs</c> (spec 0016): a signed-in account creates another company and becomes its first admin. The checks run in this order:
/// the access token (<c>401</c>, by the authorization of the route), the manifest's switch (<c>403 org_creation_disabled</c>, before the body is
/// read, so that a body that is not JSON gets it too), the body and the name (<c>400 invalid_request</c>), the account's memberships (none or the
/// account gone: <c>403 permissions_changed</c>; the token's own company need not still be one), and then one transaction: the address lock, the
/// account row <c>FOR UPDATE</c> (no company lock and no row lock of an existing membership: a sign-in holds its membership row and waits for the
/// account row, so this order cannot deadlock with it), the memberships read again and the company memberships counted
/// (<c>409 company_limit_reached</c> at the limit, nothing written), then the company, its membership and <c>org.created</c>, committed together.
/// The session is not touched: the answer sets no cookie and <c>current</c> is always <see langword="false"/>; the product calls
/// <c>POST /auth/switch</c> to go into the new company.
/// </summary>
public static class OrgCreateEndpoint
{
    public const string Path = OrgListEndpoint.Path;

    private static readonly string[] Fields = ["name"];

    public static async Task<IResult> CreateAsync(
        HttpContext http, ManifestHolder manifest, MembershipReader memberships, AuthDbContext db, CompanyService companies,
        CompanyLimitSettings limit)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(companies);
        ArgumentNullException.ThrowIfNull(limit);

        var cancellationToken = http.RequestAborted;

        // The sub of a token this service issued is always a user id; anything else is not a caller we know (as GET /auth/orgs has it).
        if (!Guid.TryParse(http.User.FindFirst(Claims.Subject)?.Value, out var userId))
        {
            return ApiResults.Error(TenancyErrors.Forbidden);
        }

        // The switch is read now, from the active manifest: a change reaches the next request. Before the body is read.
        if (!manifest.Current.OrgCreation)
        {
            return ApiResults.Error(TenancyErrors.OrgCreationDisabled);
        }

        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, Fields, cancellationToken);
        if (fields is null || !NameInput.IsValid(fields[0]))
        {
            return ApiResults.InvalidRequest();
        }

        var name = fields[0];

        // Gone, or without a membership: the token no longer matches the database. The token's own company need not still be a membership.
        if (await memberships.CountAsync(userId, cancellationToken) == 0)
        {
            return ApiResults.Error(TenancyErrors.PermissionsChanged);
        }

        var account = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Email, u.NormalizedEmail })
            .FirstOrDefaultAsync(cancellationToken);
        if (account is not { NormalizedEmail: { } normalized })
        {
            return ApiResults.Error(TenancyErrors.PermissionsChanged);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await AddressLock.AcquireAsync(db, normalized, cancellationToken);

        // The account row, locked until the transaction ends, so that a removal of its last membership (which locks the row after its own
        // membership rows) either finishes first and the account is gone here, or waits for this transaction and counts the new membership.
        var locked = await db.Database
            .SqlQuery<int>($"""SELECT 1 AS "Value" FROM "AspNetUsers" WHERE "Id" = {userId} FOR UPDATE""")
            .ToListAsync(cancellationToken);
        if (locked.Count == 0 || await memberships.CountAsync(userId, cancellationToken) == 0)
        {
            return ApiResults.Error(TenancyErrors.PermissionsChanged);
        }

        var companyCount = await db.Memberships.AsNoTracking()
            .CountAsync(m => m.UserId == userId && db.Companies.Any(c => c.Id == m.CompanyId && c.Kind == CompanyKind.Organization), cancellationToken);
        if (companyCount >= limit.MaxCompaniesPerAccount)
        {
            return ApiResults.Error(TenancyErrors.CompanyLimitReached);
        }

        var created = await companies.CreateForAccountAsync(userId, account.Email, name, cancellationToken);
        if (!created.Succeeded)
        {
            return ApiResults.Refused(created.Without);
        }

        await transaction.CommitAsync(cancellationToken);

        var company = created.Value!;
        return ApiResults.Created(new OrgListItem(
            company.CompanyId, company.Name, SpaceKinds.NameOf(CompanyKind.Organization), new RoleRef(company.RoleId, company.RoleName), Current: false));
    }
}
