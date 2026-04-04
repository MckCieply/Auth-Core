using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// Base of the sign-up tests: a host with sign-up on (<see cref="AuthAppFactory.WithSignup"/>), whose dispatcher does not run by itself
/// (<see cref="MailTestBase"/>) and whose hourly pruning pass is left out, so that a test which moves the clock by a day is not raced by it;
/// a test that needs the pass runs it by hand. The requests may name their client address (<see cref="AuditTestBase"/>).
/// </summary>
public abstract class SignupTestBase : AuditTestBase
{
    protected const string Address = "anna@firma.test";
    protected const string CompanyName = "Firma";
    protected const string Password = "Chosen-Passw0rd";

    protected static readonly TimeSpan PastTheLimit = TimeSpan.FromSeconds(61);

    protected SignupTestBase(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithSignup().WithoutHostedService<EmailPruningService>();
    }

    protected Task<List<PendingSignup>> PendingAsync() =>
        InDbAsync(db => db.PendingSignups.AsNoTracking().OrderBy(p => p.NormalizedEmail).ToListAsync(TestContext.Current.CancellationToken));

    protected Task<int> QueueCountAsync() =>
        InDbAsync(db => db.MailRequests.CountAsync(TestContext.Current.CancellationToken));

    protected Task<int> LimitCountAsync() =>
        InDbAsync(db => db.MailRequestLimits.CountAsync(TestContext.Current.CancellationToken));

    /// <summary>Asks for a sign-up (asserting the 202), runs the dispatcher and returns the token of the newest mail to the address.</summary>
    protected async Task<string> MailedAsync(string email = Address, string company = CompanyName)
    {
        using var response = await SignupApi.Request(Client, email, company);
        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        await DispatchAsync();
        return TokenIn(Mail.Sent.Last(m => string.Equals(m.To, email, StringComparison.Ordinal)));
    }

    /// <summary>
    /// An account that lost its company (spec 0009, Amendment 3): it was a member of a company, with a password (<see cref="MailTestBase.UserPassword"/>),
    /// and the membership ended straight in the database, which ends no session. That is a state the product does not make (removing a member and
    /// deleting a company revoke the sessions); it is what a completion must still cope with. Returns the id of the account.
    /// </summary>
    protected async Task<Guid> RemovedMemberAsync(string email = Address, bool confirmed = true)
    {
        var company = await CreateCompanyAsync("Former company");
        var userId = await AddMemberAsync(company, email, "user", confirmed);
        await RemoveMembershipAsync(userId);
        return userId;
    }

    /// <summary>Gives the account a company again, straight in the database: the account that "joined a company meanwhile".</summary>
    protected async Task JoinACompanyAsync(Guid userId)
    {
        var company = await CreateCompanyAsync("Newer company");
        var roleId = await RoleIdAsync(company, "user");
        await InDbAsync(async db =>
        {
            db.Memberships.Add(new Membership { UserId = userId, CompanyId = company, RoleId = roleId, JoinedAt = StorableTime.Now(Clock) });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return 0;
        });
    }
}
