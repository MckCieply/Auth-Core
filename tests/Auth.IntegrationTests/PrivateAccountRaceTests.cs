using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0010, criterion 27: a self-deletion, an invitation acceptance and a sign-up completion for one address, in every order, never leave an
/// account without a space or two accounts; a removal beside an acceptance leaves one consistent outcome. Each race holds a lock of the test and
/// lets the requests queue behind it in the order named, as <c>SignupCompleteRaceTests</c> does.
/// </summary>
public sealed class PrivateAccountRaceTests : SignupTestBase
{
    private const string Private = "ola@poczta.test";
    private const string Normalized = "OLA@POCZTA.TEST";

    public PrivateAccountRaceTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithPrivateSignup();
    }

    private async Task AssertOneAccountWithOneSpaceAsync(string normalized)
    {
        var cancellation = TestContext.Current.CancellationToken;
        var users = await InDbAsync(db => db.Users.AsNoTracking().Where(u => u.NormalizedEmail == normalized).Select(u => u.Id).ToListAsync(cancellation));
        var user = Assert.Single(users);
        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == user, cancellation)));
        Assert.Equal(0, await InDbAsync(db => db.Companies.CountAsync(
            c => c.Kind == CompanyKind.Personal && !db.Memberships.Any(m => m.CompanyId == c.Id), cancellation)));
    }

    [Theory]
    [InlineData("delete,accept,complete", 204, 204, 400)]
    [InlineData("delete,complete,accept", 204, 204, 400)]
    [InlineData("accept,delete,complete", 403, 204, 409)]
    [InlineData("accept,complete,delete", 403, 204, 409)]
    [InlineData("complete,delete,accept", 204, 204, 409)]
    [InlineData("complete,accept,delete", 403, 204, 409)]
    public async Task A_self_deletion_an_acceptance_and_a_completion_never_leave_an_account_without_a_space_or_two_accounts(
        string order, int deleted, int accepted, int completed)   // criterion 27
    {
        await CreatePrivateAccountAsync(Private);
        var session = await SessionApi.LoginAsync(Client, Private, UserPassword);
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, Private, "user");
        await EnqueueInvitationAsync(invite, Private);
        await DispatchAsync();
        var inviteToken = TokenIn(Mail.Sent.Last(m => m.To == Private));
        var signupToken = await PendingSignupSeed.IssueAsync(Factory.Services, Private, null, SignupKind.Private);

        var started = new Dictionary<string, Task<HttpResponseMessage>>(StringComparer.Ordinal);
        await using (var held = await HeldLock.AddressAsync(Factory, Normalized))
        {
            var waiting = 0;
            foreach (var step in order.Split(','))
            {
                started[step] = step switch
                {
                    "delete" => TenancyApi.Send(Client, HttpMethod.Delete, "/auth/account", session.AccessToken, new { password = UserPassword }),
                    "accept" => TenancyApi.AcceptLeaving(Client, inviteToken, "Invited-Passw0rd"),
                    _ => SignupApi.CompletePrivate(Client, signupToken, Password),
                };
                await HeldLock.WaitForWaitersAsync(Factory, ++waiting);
            }

            await held.ReleaseAsync();
        }

        using var deletion = await started["delete"];
        using var acceptance = await started["accept"];
        using var completion = await started["complete"];
        Assert.Equal((HttpStatusCode)deleted, deletion.StatusCode);
        Assert.Equal((HttpStatusCode)accepted, acceptance.StatusCode);
        Assert.Equal((HttpStatusCode)completed, completion.StatusCode);
        await AssertOneAccountWithOneSpaceAsync(Normalized);
    }

    [Fact]
    public async Task An_invitation_move_and_a_self_deletion_of_the_same_account_started_together_end_with_one_winner_and_no_deadlock()   // criterion 27
    {
        // Nothing holds a lock here: the two requests meet on their own, as they would in life, several times over.
        for (var round = 0; round < 6; round++)
        {
            var email = $"round{round}@poczta.test";
            await CreatePrivateAccountAsync(email);
            var session = await SessionApi.LoginAsync(Client, email, UserPassword);
            var company = await CreateCompanyAsync($"Acme {round}");
            var invite = await AddInviteAsync(company, email, "user");
            await EnqueueInvitationAsync(invite, email);
            await DispatchAsync();
            var inviteToken = TokenIn(Mail.Sent.Last(m => m.To == email));

            var deletion = TenancyApi.Send(Client, HttpMethod.Delete, "/auth/account", session.AccessToken, new { password = UserPassword });
            var acceptance = TenancyApi.AcceptLeaving(Client, inviteToken, "Invited-Passw0rd");
            using var deleted = await deletion;
            using var accepted = await acceptance;

            // Either the move came first (the deletion then finds a company: permissions_changed), or the deletion did (the invitation then makes
            // a new account). Never a 500, never an account without a space, never two.
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
            Assert.True(
                deleted.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Forbidden,
                $"Round {round}: the deletion answered {(int)deleted.StatusCode}.");
            var normalized = email.ToUpperInvariant();
            await AssertOneAccountWithOneSpaceAsync(normalized);
            var organization = await InDbAsync(db => db.Memberships.CountAsync(
                m => m.CompanyId == company && db.Users.Any(u => u.Id == m.UserId && u.NormalizedEmail == normalized), TestContext.Current.CancellationToken));
            Assert.Equal(1, organization);   // the invited person is a member of the company, whoever came first
        }
    }

    [Fact]
    public async Task A_removal_not_yet_committed_makes_a_parallel_acceptance_already_member_and_the_invitation_stays()   // criterion 27
    {
        var (acme, _, adminToken) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var worker = await AddMemberAsync(acme, "worker@acme.test", "user");
        var globex = await CreateCompanyAsync("Globex");
        var invite = await AddInviteAsync(globex, "worker@acme.test", "user");
        await EnqueueInvitationAsync(invite, "worker@acme.test");
        await DispatchAsync();
        var token = TokenIn(Mail.Sent.Last(m => m.To == "worker@acme.test"));

        await using (var held = await HeldLock.AuditTableAsync(Factory))
        {
            var removal = TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/members/{worker}", adminToken);
            await HeldLock.WaitForWaitersAsync(Factory, 1);   // the account's rows are deleted, not committed: the removal waits at its audit rows

            using (var early = await TenancyApi.Accept(Client, token, "Invited-Passw0rd"))
            {
                await TenancyApi.AssertErrorAsync(early, HttpStatusCode.Conflict, "already_member");   // it saw the committed state: a member
            }

            await held.ReleaseAsync();
            using var removed = await removal;
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        using var later = await TenancyApi.Accept(Client, token, "Invited-Passw0rd");   // the invitation stayed usable
        Assert.Equal(HttpStatusCode.NoContent, later.StatusCode);
        await AssertOneAccountWithOneSpaceAsync("WORKER@ACME.TEST");
        Assert.Equal(0, await InDbAsync(db => db.Users.CountAsync(u => u.Id == worker, TestContext.Current.CancellationToken)));   // a new account
    }

    [Fact]
    public async Task A_removal_that_commits_between_the_read_of_the_account_and_the_read_of_its_space_makes_a_new_account()   // criterion 27
    {
        var (acme, _, _) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var worker = await AddMemberAsync(acme, "worker@acme.test", "user");
        var globex = await CreateCompanyAsync("Globex");
        var invite = await AddInviteAsync(globex, "worker@acme.test", "user");
        await EnqueueInvitationAsync(invite, "worker@acme.test");
        await DispatchAsync();
        var token = TokenIn(Mail.Sent.Last(m => m.To == "worker@acme.test"));

        Task<HttpResponseMessage> acceptance;
        await using (var held = await HeldLock.MembershipsAsync(Factory))
        {
            acceptance = TenancyApi.Accept(Client, token, "Invited-Passw0rd");
            await HeldLock.WaitForWaitersAsync(Factory, 1);   // the account is read; the read of its space waits for the memberships
            await held.DeleteAccountAsync(worker);            // the removal's effect: the account and its membership go, and commit with the release
            await held.ReleaseAsync();
        }

        using var accepted = await acceptance;
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);   // never a 500: the address has no account now, and the password sent makes one
        await AssertOneAccountWithOneSpaceAsync("WORKER@ACME.TEST");
        Assert.Equal(0, await InDbAsync(db => db.Users.CountAsync(u => u.Id == worker, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.CompanyId == globex, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await InDbAsync(db => db.Invites.CountAsync(i => i.Id == invite, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task An_acceptance_that_waits_for_the_address_while_a_removal_commits_makes_a_new_account()   // criterion 27
    {
        var (acme, _, adminToken) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var worker = await AddMemberAsync(acme, "worker@acme.test", "user");
        var globex = await CreateCompanyAsync("Globex");
        var invite = await AddInviteAsync(globex, "worker@acme.test", "user");
        await EnqueueInvitationAsync(invite, "worker@acme.test");
        await DispatchAsync();
        var token = TokenIn(Mail.Sent.Last(m => m.To == "worker@acme.test"));

        Task<HttpResponseMessage> acceptance;
        await using (var held = await HeldLock.AddressAsync(Factory, "WORKER@ACME.TEST"))
        {
            acceptance = TenancyApi.Accept(Client, token, "Invited-Passw0rd");
            await HeldLock.WaitForWaitersAsync(Factory, 1);   // the invitation used up, waiting for the address
            using (var removed = await TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/members/{worker}", adminToken))
            {
                Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);   // a removal takes no address lock
            }

            await held.ReleaseAsync();
        }

        using var accepted = await acceptance;
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        await AssertOneAccountWithOneSpaceAsync("WORKER@ACME.TEST");
        Assert.Equal(0, await InDbAsync(db => db.Users.CountAsync(u => u.Id == worker, TestContext.Current.CancellationToken)));
    }
}
