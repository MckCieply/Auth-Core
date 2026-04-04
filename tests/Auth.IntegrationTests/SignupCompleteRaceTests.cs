using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>
/// The races of a completion. None of them waits for luck: each one either holds a lock in a transaction of the test or pauses the mail
/// transport inside the dispatcher's transaction, starts the requests, waits until the database says how many of them are queued behind a
/// lock (in this database only), and then lets go, so that the interleaving the test names is the one that happens.
/// </summary>
public sealed class SignupCompleteRaceTests(PostgresFixture postgres, KeyMaterialFixture keys) : SignupTestBase(postgres, keys)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    /// <summary>A transaction of the test that holds a lock until <see cref="ReleaseAsync"/> (or, failing that, until it is disposed: rolled back).</summary>
    private sealed class Held(IServiceScope scope, IDbContextTransaction transaction) : IAsyncDisposable
    {
        public Task ReleaseAsync() => transaction.CommitAsync(TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            scope.Dispose();
        }
    }

    /// <summary>The advisory lock of an address, which a completion and an invitation acceptance both take.</summary>
    private async Task<Held> HoldAddressAsync(string normalizedEmail)
    {
        var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({normalizedEmail}, 0))", TestContext.Current.CancellationToken);
        return new Held(scope, transaction);
    }

    /// <summary>A lock on the audit table that stops every write to it: the completion that reaches its audit row waits there.</summary>
    private async Task<Held> HoldAuditTableAsync()
    {
        var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE audit_events IN SHARE MODE", TestContext.Current.CancellationToken);
        return new Held(scope, transaction);
    }

    /// <summary>Waits until exactly this many backends of this test's database are waiting for a lock.</summary>
    private async Task WaitForWaitersAsync(int count)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            var waiting = await InDbAsync(db => db.Database
                .SqlQuery<long>($"""SELECT count(*) AS "Value" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'""")
                .SingleAsync(TestContext.Current.CancellationToken));
            if (waiting == count)
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Expected {count} backends waiting for a lock, saw {waiting}.");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Of_parallel_completions_with_one_token_exactly_one_is_200()   // criterion 12, Review Focus 3
    {
        var token = await MailedAsync();

        // All eight have found the link good and are queued for the lock of the address before any of them uses it up.
        Task<HttpResponseMessage>[] requests;
        await using (var held = await HoldAddressAsync("ANNA@FIRMA.TEST"))
        {
            requests = [.. Enumerable.Range(0, 8).Select(_ => SignupApi.Complete(Client, token, Password, CompanyName))];
            await WaitForWaitersAsync(8);
            await held.ReleaseAsync();
        }

        var responses = await Task.WhenAll(requests);
        try
        {
            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
            var refused = responses.Where(r => r.StatusCode != HttpStatusCode.OK).ToList();
            Assert.Equal(7, refused.Count);
            foreach (var response in refused)
            {
                await AccountApi.AssertErrorAsync(response, AccountApi.InvalidToken);
            }
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        Assert.Equal(1, await InDbAsync(db => db.Users.CountAsync(u => u.Email == Address, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(c => c.Name == CompanyName, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(
            m => db.Companies.Any(c => c.Id == m.CompanyId && c.Name == CompanyName), TestContext.Current.CancellationToken)));
        Assert.Empty(await PendingAsync());
        Assert.Equal(1L, await InDbAsync(db => db.Database.SqlQuery<long>($"""SELECT count(*) AS "Value" FROM "OpenIddictAuthorizations" """).SingleAsync(TestContext.Current.CancellationToken)));   // the one winner made the one session
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_completion_and_an_invitation_acceptance_for_one_address_never_both_create_an_account(bool completionFirst)   // criterion 14
    {
        var company = await CreateCompanyAsync("Acme");
        var address = completionFirst ? "completion-first@firma.test" : "acceptance-first@firma.test";
        var normalized = address.ToUpperInvariant();
        const string Name = "Firma Race";
        var signupToken = await MailedAsync(address, Name);
        var invite = await AddInviteAsync(company, address, "user");
        await EnqueueInvitationAsync(invite, address);
        await DispatchAsync();
        var inviteToken = TokenIn(Mail.Sent.Last(m => m.To == address));

        // Both have done everything up to the lock of the address and wait for it, in the order the test names; the lock queue is first come, first served.
        Task<HttpResponseMessage> complete;
        Task<HttpResponseMessage> accept;
        await using (var held = await HoldAddressAsync(normalized))
        {
            if (completionFirst)
            {
                complete = SignupApi.Complete(Client, signupToken, Password, Name);
                await WaitForWaitersAsync(1);
                accept = TenancyApi.Accept(Client, inviteToken, "Invited-Passw0rd");
            }
            else
            {
                accept = TenancyApi.Accept(Client, inviteToken, "Invited-Passw0rd");
                await WaitForWaitersAsync(1);
                complete = SignupApi.Complete(Client, signupToken, Password, Name);
            }

            await WaitForWaitersAsync(2);
            await held.ReleaseAsync();
        }

        using var completed = await complete;
        using var accepted = await accept;

        if (completionFirst)
        {
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, accepted.StatusCode);
            Assert.Equal("""{"error":"already_member"}""", await accepted.Content.ReadAsStringAsync());
            Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(c => c.Name == Name, TestContext.Current.CancellationToken)));
        }
        else
        {
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
            await SignupApi.AssertAlreadyRegisteredAsync(completed);
            Assert.Equal(0, await InDbAsync(db => db.Companies.CountAsync(c => c.Name == Name, TestContext.Current.CancellationToken)));
        }

        var user = await InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.NormalizedEmail == normalized, TestContext.Current.CancellationToken));
        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == user.Id, TestContext.Current.CancellationToken)));
        Assert.DoesNotContain(await PendingAsync(), p => p.NormalizedEmail == normalized);
    }

    [Fact]
    public async Task Of_parallel_completions_for_an_account_without_a_company_exactly_one_is_200()   // criteria 12 and 24
    {
        var userId = await RemovedMemberAsync();
        var token = await MailedAsync();

        Task<HttpResponseMessage>[] requests;
        await using (var held = await HoldAddressAsync("ANNA@FIRMA.TEST"))
        {
            requests = [.. Enumerable.Range(0, 6).Select(_ => SignupApi.CompleteWithoutPassword(Client, token, CompanyName))];
            await WaitForWaitersAsync(6);
            await held.ReleaseAsync();
        }

        var responses = await Task.WhenAll(requests);
        try
        {
            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
            foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
            {
                await AccountApi.AssertErrorAsync(refused, AccountApi.InvalidToken);
            }
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(c => c.Name == CompanyName, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == userId, TestContext.Current.CancellationToken)));
        Assert.Empty(await PendingAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_comeback_and_an_invitation_acceptance_for_one_account_never_both_give_it_a_company(bool completionFirst)   // criterion 24: the 409 race
    {
        var company = await CreateCompanyAsync("Acme");
        var address = completionFirst ? "back-first@firma.test" : "invite-first@firma.test";
        var normalized = address.ToUpperInvariant();
        const string Name = "Firma Race";
        var userId = await RemovedMemberAsync(address);
        var signupToken = await MailedAsync(address, "Asked for");   // an account without a company gets the link
        var invite = await AddInviteAsync(company, address, "user");
        await EnqueueInvitationAsync(invite, address);
        await DispatchAsync();
        var inviteToken = TokenIn(Mail.Sent.Last(m => m.To == address));

        // Both have done everything up to the lock of the address and wait for it, in the order the test names.
        Task<HttpResponseMessage> complete;
        Task<HttpResponseMessage> accept;
        await using (var held = await HoldAddressAsync(normalized))
        {
            if (completionFirst)
            {
                complete = SignupApi.CompleteWithoutPassword(Client, signupToken, Name);
                await WaitForWaitersAsync(1);
                accept = TenancyApi.Accept(Client, inviteToken, "Invited-Passw0rd");
            }
            else
            {
                accept = TenancyApi.Accept(Client, inviteToken, "Invited-Passw0rd");
                await WaitForWaitersAsync(1);
                complete = SignupApi.CompleteWithoutPassword(Client, signupToken, Name);
            }

            await WaitForWaitersAsync(2);
            await held.ReleaseAsync();
        }

        using var completed = await complete;
        using var accepted = await accept;

        if (completionFirst)
        {
            _ = await SessionApi.ReadSessionAsync(completed);
            Assert.Equal(HttpStatusCode.Conflict, accepted.StatusCode);
            Assert.Equal("""{"error":"already_member"}""", await accepted.Content.ReadAsStringAsync());
            Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(c => c.Name == Name, TestContext.Current.CancellationToken)));
            _ = await SessionApi.LoginAsync(Client, address, UserPassword);   // the invitation did not touch the password
        }
        else
        {
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
            await SignupApi.AssertAlreadyRegisteredAsync(completed);
            Assert.Equal(0, await InDbAsync(db => db.Companies.CountAsync(c => c.Name == Name, TestContext.Current.CancellationToken)));
            var login = await SessionApi.LoginAsync(Client, address, "Invited-Passw0rd");   // the acceptance set its password, as it does for any existing account
            Assert.Equal(company.ToString(), AccessTokens.Text(login.AccessToken, "org_id"));
        }

        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == userId, TestContext.Current.CancellationToken)));
        Assert.DoesNotContain(await PendingAsync(), p => p.NormalizedEmail == normalized);
    }

    [Fact]
    public async Task A_mail_that_replaces_the_link_while_a_completion_waits_for_it_makes_the_completion_invalid_token()   // the dispatcher locks the pending sign-up
    {
        var first = await MailedAsync();
        Clock.Advance(PastTheLimit);
        using (var again = await SignupApi.Request(Client, Address, CompanyName))
        {
            Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);   // a second mail is queued
        }

        // The dispatcher has locked the pending sign-up and issued the new link, and is sending the mail: its transaction is open.
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Mail.DuringSend = async () =>
        {
            reached.TrySetResult();
            await proceed.Task.WaitAsync(Patience);   // a failing step of the test cannot park the dispatcher for good
        };
        var dispatch = DispatchAsync();
        Task<HttpResponseMessage> complete;
        try
        {
            await reached.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

            // The completion still finds the old link good, and waits for the row that the mail has locked.
            complete = SignupApi.Complete(Client, first, Password, CompanyName);
            await WaitForWaitersAsync(1);
            Assert.False(complete.IsCompleted);
        }
        finally
        {
            proceed.TrySetResult();
        }

        Assert.Equal(1, await dispatch);
        using var done = await complete;

        await AccountApi.AssertErrorAsync(done, AccountApi.InvalidToken);
        Assert.Equal(0, await InDbAsync(db => db.Users.CountAsync(u => u.Email == Address, TestContext.Current.CancellationToken)));
        Assert.Equal(2, Mail.Sent.Count);
        using var second = await SignupApi.Complete(Client, TokenIn(Mail.Sent[1]), Password, CompanyName);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, await InDbAsync(db => db.Users.CountAsync(u => u.Email == Address, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_mail_that_waits_for_a_completion_never_leaves_a_working_link_to_a_used_sign_up()   // the dispatcher locks the pending sign-up and looks the account up again
    {
        var first = await MailedAsync();
        Clock.Advance(PastTheLimit);
        using (var again = await SignupApi.Request(Client, Address, CompanyName))
        {
            Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);   // a second mail is queued
        }

        Task<HttpResponseMessage> complete;
        Task<int> dispatch;
        await using (var held = await HoldAuditTableAsync())
        {
            // The completion has used the link up and made the account, and waits to write its audit rows: its transaction is open.
            complete = SignupApi.Complete(Client, first, Password, CompanyName);
            await WaitForWaitersAsync(1);

            // The mail finds the pending sign-up still there, and waits for the completion's lock on it.
            dispatch = DispatchAsync();
            await WaitForWaitersAsync(2);
            Assert.False(dispatch.IsCompleted);
            await held.ReleaseAsync();
        }

        using var done = await complete;
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Equal(1, await dispatch);

        // The sign-up is used: the queued mail found the account and said so, with no link.
        Assert.Equal(2, Mail.Sent.Count);
        Assert.StartsWith("You already have an account", Mail.Sent[1].Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", Mail.Sent[1].TextBody, StringComparison.Ordinal);
        Assert.Empty(await PendingAsync());
        Assert.Equal(1, await InDbAsync(db => db.Users.CountAsync(u => u.Email == Address, TestContext.Current.CancellationToken)));
        Assert.Single(await AuditAsync(AuditKinds.SignupCompleted));
    }
}
