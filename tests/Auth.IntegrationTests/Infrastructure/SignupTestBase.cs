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
}
