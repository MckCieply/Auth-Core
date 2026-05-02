using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0011, criterion 20: of a finish, a sign-up completion and an invitation acceptance running at once for one address, at most one makes the
/// account: they take the same address lock. Each race holds that lock in the test and lets the requests queue behind it in the order named, as
/// <c>PrivateAccountRaceTests</c> does. The finish's other "meanwhile" branches (spec 0011 → finish, steps 7 and 8) run the same way: the world
/// changes while the finish waits for the lock.
/// </summary>
public sealed class ProviderFinishRaceTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Racer = "race@gmail.test";
    private const string Normalized = "RACE@GMAIL.TEST";
    private const string Subject = "600000000000000000006";

    private Task<int> AccountsAsync() => InDbAsync(db => db.Users.CountAsync(u => u.NormalizedEmail == Normalized, TestContext.Current.CancellationToken));

    private Task<int> LoginsAsync() => InDbAsync(db => db.UserLogins.CountAsync(TestContext.Current.CancellationToken));

    private Task<int> SpacesAsync() => InDbAsync(db => db.Companies.CountAsync(TestContext.Current.CancellationToken));

    /// <summary>Sends the finish while the address lock is held, waits until it is queued behind it, runs <paramref name="change"/>, releases.</summary>
    private async Task<HttpResponseMessage> FinishWhileLockedAsync(ProviderApi.Ticket ticket, Func<Task> change)
    {
        await using var held = await HeldLock.AddressAsync(Factory, Normalized);
        var finish = ProviderApi.Finish(Client, ticket, "private");
        await HeldLock.WaitForWaitersAsync(Factory, 1, finish);
        await change();
        await held.ReleaseAsync();
        return await finish;
    }

    [Fact]
    public async Task A_finish_a_completion_and_an_acceptance_for_one_address_make_one_account()   // criterion 20
    {
        var ticket = await TicketAsync("google", GooglePerson(email: Racer, subject: Subject));
        var signupToken = await PendingSignupSeed.IssueAsync(Factory.Services, Racer, "Firma");
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, Racer, "user");
        await EnqueueInvitationAsync(invite, Racer);
        await DispatchAsync();
        var inviteToken = TokenIn(Mail.Sent.Last(m => m.To == Racer));

        Task<HttpResponseMessage> finish, completion, acceptance;
        await using (var held = await HeldLock.AddressAsync(Factory, Normalized))
        {
            finish = ProviderApi.Finish(Client, ticket, "private");
            await HeldLock.WaitForWaitersAsync(Factory, 1, finish);
            completion = SignupApi.Complete(Client, signupToken, Password, "Firma");
            await HeldLock.WaitForWaitersAsync(Factory, 2, finish, completion);
            acceptance = TenancyApi.Accept(Client, inviteToken, "Invited-Passw0rd");
            await HeldLock.WaitForWaitersAsync(Factory, 3, finish, completion, acceptance);
            await held.ReleaseAsync();
        }

        using var finished = await finish;
        using var completed = await completion;
        using var accepted = await acceptance;
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        // The finish deleted the pending sign-up of the address: the link of the mail is used up by then.
        await TenancyApi.AssertErrorAsync(completed, HttpStatusCode.BadRequest, "invalid_token");
        await TenancyApi.AssertErrorAsync(accepted, HttpStatusCode.Conflict, "private_account_exists");
        Assert.Equal(1, await AccountsAsync());
        Assert.Equal(1, await LoginsAsync());
        Assert.Single(await AuditAsync(AuditKinds.SignupCompleted));
        Assert.Equal(0, await TicketCountAsync());
    }

    [Theory]
    [InlineData("completion")]
    [InlineData("acceptance")]
    public async Task A_completion_or_an_acceptance_ahead_of_a_finish_makes_the_account_and_the_finish_is_409_with_its_ticket_kept(string first)   // criterion 20, the other order
    {
        var ticket = await TicketAsync("google", GooglePerson(email: Racer, subject: Subject));
        Func<Task<HttpResponseMessage>> ahead;
        if (first == "completion")
        {
            var signupToken = await PendingSignupSeed.IssueAsync(Factory.Services, Racer, "Firma");
            ahead = () => SignupApi.Complete(Client, signupToken, Password, "Firma");
        }
        else
        {
            var company = await CreateCompanyAsync("Acme");
            var invite = await AddInviteAsync(company, Racer, "user");
            await EnqueueInvitationAsync(invite, Racer);
            await DispatchAsync();
            var inviteToken = TokenIn(Mail.Sent.Last(m => m.To == Racer));
            ahead = () => TenancyApi.Accept(Client, inviteToken, "Invited-Passw0rd");
        }

        Task<HttpResponseMessage> made, finish;
        await using (var held = await HeldLock.AddressAsync(Factory, Normalized))
        {
            made = ahead();
            await HeldLock.WaitForWaitersAsync(Factory, 1, made);
            finish = ProviderApi.Finish(Client, ticket, "private");
            await HeldLock.WaitForWaitersAsync(Factory, 2, made, finish);
            await held.ReleaseAsync();
        }

        using var madeAnswer = await made;
        using var finished = await finish;
        Assert.True(madeAnswer.IsSuccessStatusCode);   // 200 for the completion, 204 for the acceptance
        await SignupApi.AssertAlreadyRegisteredAsync(finished);
        Assert.Equal(1, await AccountsAsync());
        Assert.Equal(1, await TicketCountAsync());   // the refused finish leaves its ticket (spec 0011 → tickets: a refusal changes nothing)
        Assert.Equal(0, await LoginsAsync());
        Assert.DoesNotContain(await AuditAsync(AuditKinds.LoginSucceeded), row => AuditApi.Text(row, "provider") is not null);
        var completed = await AuditAsync(AuditKinds.SignupCompleted);
        Assert.Equal(first == "completion" ? 1 : 0, completed.Count);   // the completion's own: never the finish's
        Assert.All(completed, row => Assert.Null(AuditApi.Text(row, "provider")));
    }

    [Fact]
    public async Task Two_finishes_with_two_tickets_for_one_address_make_one_account()   // criterion 20
    {
        var first = await TicketAsync("google", GooglePerson(email: Racer, subject: Subject));
        var second = await TicketAsync("microsoft", PersonalMicrosoft(email: Racer));
        var spaces = await SpacesAsync();

        Task<HttpResponseMessage> one, two;
        await using (var held = await HeldLock.AddressAsync(Factory, Normalized))
        {
            one = ProviderApi.Finish(Client, first, "private");
            await HeldLock.WaitForWaitersAsync(Factory, 1, one);
            two = ProviderApi.Finish(Client, second, "organization", "Firma");
            await HeldLock.WaitForWaitersAsync(Factory, 2, one, two);
            await held.ReleaseAsync();
        }

        using var made = await one;
        using var refused = await two;
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        await SignupApi.AssertAlreadyRegisteredAsync(refused);
        Assert.Equal(1, await AccountsAsync());
        Assert.Single(await InDbAsync(db => db.UserLogins.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(1, await TicketCountAsync());   // the refused ticket stays until it expires
        Assert.Equal(spaces + 1, await SpacesAsync());   // the personal space of the first, no company of the second
        Assert.Single(await AuditAsync(AuditKinds.SignupCompleted));
        Assert.DoesNotContain(await AuditAsync(AuditKinds.OrgCreated), row => AuditApi.Text(row, "via") == "signup");
    }

    [Fact]
    public async Task Two_finishes_with_one_ticket_give_exactly_one_account_one_session_and_one_signup_completed()   // criterion 20
    {
        var ticket = await TicketAsync("google", GooglePerson(email: Racer, subject: Subject));
        Task<HttpResponseMessage> first, second;
        await using (var held = await HeldLock.AddressAsync(Factory, Normalized))
        {
            first = ProviderApi.Finish(Client, ticket, "private");
            second = ProviderApi.Finish(Client, ticket, "private");
            await HeldLock.WaitForWaitersAsync(Factory, 2, first, second);
            await held.ReleaseAsync();
        }

        using var a = await first;
        using var b = await second;

        var ok = new[] { a, b }.Where(r => r.StatusCode == HttpStatusCode.OK).ToList();
        var refused = new[] { a, b }.Where(r => r.StatusCode != HttpStatusCode.OK).ToList();
        Assert.Single(ok);
        await TenancyApi.AssertErrorAsync(Assert.Single(refused), HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.False(refused[0].Headers.Contains("Set-Cookie"));
        _ = await SessionApi.ReadSessionAsync(ok[0]);
        ProviderApi.AssertBindingCleared(ok[0]);
        Assert.Equal(1, await AccountsAsync());
        Assert.Equal(1, await LoginsAsync());
        Assert.Single(await AuditAsync(AuditKinds.SignupCompleted));
        Assert.Single(await AuditAsync(AuditKinds.LoginSucceeded));
        Assert.Equal(0, await TicketCountAsync());
    }

    [Fact]
    public async Task A_ticket_used_up_while_the_finish_waited_for_the_address_lock_is_400_invalid_ticket_and_makes_nothing()   // spec 0011 → finish, step 8
    {
        var ticket = await TicketAsync("google", GooglePerson(email: Racer, subject: Subject));
        var spaces = await SpacesAsync();

        using var response = await FinishWhileLockedAsync(ticket, () => InDbAsync(db => db.ProviderTickets.ExecuteDeleteAsync(TestContext.Current.CancellationToken)));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(0, await AccountsAsync());
        Assert.Equal(0, await LoginsAsync());
        Assert.Equal(spaces, await SpacesAsync());
        Assert.Empty(await AuditAsync(AuditKinds.SignupCompleted));
        Assert.Empty(await AuditAsync(AuditKinds.LoginSucceeded));
    }

    [Fact]
    public async Task A_ticket_that_expired_while_the_finish_waited_for_the_address_lock_is_400_invalid_ticket_and_makes_nothing()   // spec 0011 → finish, step 8
    {
        var ticket = await TicketAsync("google", GooglePerson(email: Racer, subject: Subject));

        using var response = await FinishWhileLockedAsync(ticket, () =>
        {
            Clock.Advance(TimeSpan.FromMinutes(11));
            return Task.CompletedTask;
        });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.Equal(0, await AccountsAsync());
        Assert.Equal(0, await LoginsAsync());
        Assert.Empty(await AuditAsync(AuditKinds.SignupCompleted));
    }

    [Fact]
    public async Task An_identity_linked_while_the_finish_waited_is_409_already_registered_and_the_ticket_stays()   // spec 0011 → finish, step 7
    {
        var other = await CreateUserAsync("someone@gmail.test", confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson(email: Racer, subject: Subject));

        using var response = await FinishWhileLockedAsync(ticket, () => LinkAsync(other.Id, "google", Subject));

        await SignupApi.AssertAlreadyRegisteredAsync(response);
        Assert.Equal(0, await AccountsAsync());
        Assert.Equal(1, await LoginsAsync());   // the one linked meanwhile
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await AuditAsync(AuditKinds.SignupCompleted));
        Assert.Empty(await AuditAsync(AuditKinds.LoginSucceeded));
    }

    [Fact]
    public async Task An_account_made_under_the_address_while_the_finish_waited_is_409_already_registered_and_the_ticket_stays()   // spec 0011 → finish, step 7
    {
        var ticket = await TicketAsync("google", GooglePerson(email: Racer, subject: Subject));

        using var response = await FinishWhileLockedAsync(ticket, () => CreateUserAsync(Racer, confirmed: false));

        await SignupApi.AssertAlreadyRegisteredAsync(response);
        Assert.Equal(1, await AccountsAsync());
        Assert.False(await InDbAsync(db => db.Users.Where(u => u.NormalizedEmail == Normalized).Select(u => u.EmailConfirmed).SingleAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(0, await LoginsAsync());
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await AuditAsync(AuditKinds.SignupCompleted));
        Assert.Empty(await AuditAsync(AuditKinds.LoginSucceeded));
    }
}
