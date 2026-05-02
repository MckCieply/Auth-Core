using System.Data.Common;
using System.Net;
using Auth.FakeOidc;
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → <c>DELETE /auth/account</c> with a provider confirmation (criteria 23 and 24).</summary>
public sealed class AccountDeletionProviderTests : ProviderTestBase
{
    private const string Ola = FakePeople.GoogleEmail;
    private const string Normalized = "OLA@GMAIL.TEST";
    private const string PersonalKey = FakePeople.PersonalTenant + ":" + FakePeople.PersonalObjectId;

    // Spelled out, so that the escape in a test body is not read as an escape of this file.
    private const char Backslash = (char)0x5C;

    private readonly DeleteFaultInterceptor _fault = new();

    public AccountDeletionProviderTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithServices(services => services.ConfigureDbContext<AuthDbContext>(options => options.AddInterceptors(_fault)));
    }

    private Task<int> UsersAsync(Guid userId) => InDbAsync(db => db.Users.CountAsync(u => u.Id == userId, TestContext.Current.CancellationToken));

    private Task<int> StreaksAsync() => InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken));

    /// <summary>Sends the deletion with the ticket while the address lock is held, waits until it is queued behind it, runs <paramref name="change"/>, releases.</summary>
    private async Task<HttpResponseMessage> DeleteWhileLockedAsync(string accessToken, ProviderApi.Ticket ticket, Func<Task> change)
    {
        await using var held = await HeldLock.AddressAsync(Factory, Normalized);
        var pending = ProviderApi.DeleteAccount(Client, accessToken, new { provider_ticket = ticket.Value }, ticket.Binding);
        await HeldLock.WaitForWaitersAsync(Factory, 1, pending);
        await change();
        await held.ReleaseAsync();
        return await pending;
    }

    private static string Token43(char c) => new(c, 43);

    [Fact]
    public async Task A_private_account_without_a_password_deletes_itself_with_a_reauth_ticket()   // criterion 23
    {
        var (userId, session) = await PrivateThroughProviderAsync("google", GooglePerson());
        await LinkAsync(userId, "microsoft", PersonalKey);   // a second login: the deletion removes both (spec 0011 → To verify)
        var reauth = await TicketAsync("google", GooglePerson(), "?purpose=reauth");
        var cancellation = TestContext.Current.CancellationToken;

        using var response = await ProviderApi.DeleteAccount(Client, session.AccessToken, new { provider_ticket = reauth.Value }, reauth.Binding);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.Equal(TimeSpan.Zero, SessionApi.RefreshCookieOf(response).MaxAge);
        ProviderApi.AssertBindingCleared(response);
        Assert.Equal(0, await UsersAsync(userId));
        Assert.Equal(0, await InDbAsync(db => db.UserLogins.CountAsync(l => l.UserId == userId, cancellation)));
        Assert.Equal(0, await TicketCountAsync());
        var reauthenticated = await SingleAsync(AuditKinds.ProviderReauthenticated);
        Assert.Equal(userId, reauthenticated.ActorUserId);
        Assert.Equal(userId, reauthenticated.SubjectUserId);
        Assert.Equal(Ola, reauthenticated.SubjectEmail);
        Assert.NotNull(reauthenticated.OrgId);
        Assert.Equal(PersonalSpaces.Name, reauthenticated.OrgName);
        Assert.Equal(["provider"], AuditApi.DetailNames(reauthenticated));
        Assert.Equal("google", AuditApi.Text(reauthenticated, "provider"));
        Assert.Equal("self", AuditApi.Text(await SingleAsync(AuditKinds.AccountDeleted), "reason"));
        Assert.Empty(await AuditAsync(AuditKinds.AccountDeleteRefused));
        using var refresh = await SessionApi.Refresh(Client, session.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(refresh);
    }

    [Fact]
    public async Task A_deletion_by_the_microsoft_identity_names_microsoft_in_the_row_it_writes()   // criterion 23: details.provider is the ticket's
    {
        var (userId, session) = await PrivateThroughProviderAsync("microsoft", PersonalMicrosoft());
        await LinkAsync(userId, "google", FakePeople.GoogleSubject);
        var reauth = await TicketAsync("microsoft", PersonalMicrosoft(), "?purpose=reauth");

        using var response = await ProviderApi.DeleteAccount(Client, session.AccessToken, new { provider_ticket = reauth.Value }, reauth.Binding);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("microsoft", AuditApi.Text(await SingleAsync(AuditKinds.ProviderReauthenticated), "provider"));
        Assert.Equal(0, await UsersAsync(userId));
    }

    [Fact]
    public async Task A_deletion_with_the_password_clears_auth_pt_beside_the_refresh_cookie()   // spec 0011 → the deletion: every 204 clears both
    {
        var (userId, _) = await CreatePrivateAccountAsync(Ola);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);

        using var response = await ProviderApi.DeleteAccount(Client, session.AccessToken, new { password = UserPassword }, null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(TimeSpan.Zero, SessionApi.RefreshCookieOf(response).MaxAge);
        ProviderApi.AssertBindingCleared(response);
        Assert.Equal(0, await UsersAsync(userId));
        Assert.Empty(await AuditAsync(AuditKinds.ProviderReauthenticated));
    }

    public static TheoryData<string> BadBodies => new()
    {
        "{}",
        """{"password":"Another-Passw0rd","provider_ticket":"x"}""",
        """{"password":"Another-Passw0rd","provider_ticket":null}""",
        """{"password":"","provider_ticket":"x"}""",
        """{"password":"  "}""",
        """{"password":5}""",
        """{"provider_ticket":5}""",
        """{"provider_ticket":null}""",
        """{"provider_ticket":["x"]}""",
        """{"provider_ticket":{"a":"x"}}""",
        """{"provider_ticket":true}""",
        """["provider_ticket"]""",
        "{\"password\":\"Another" + Backslash + "u0000Passw0rd\"}",   // NUL in the password
        "{\"provider_ticket\":\"" + Backslash + "ud800\"}",   // an unpaired surrogate escape is not a string
    };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task Both_members_neither_or_one_of_the_wrong_type_is_400_invalid_request(string body)   // criterion 24
    {
        var (userId, session) = await PrivateThroughProviderAsync("google", GooglePerson());
        await TicketAsync("google", GooglePerson(), "?purpose=reauth");

        using var response = await ProviderApi.DeleteAccountRaw(Client, session.AccessToken, body);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(1, await UsersAsync(userId));
        Assert.Equal(1, await TicketCountAsync());   // the body is judged before any ticket is touched
        Assert.Equal(0, await StreaksAsync());
        Assert.Empty(await AuditAsync(AuditKinds.AccountDeleteRefused));
    }

    [Theory]
    [InlineData("""{"provider_ticket":""}""")]
    [InlineData("""{"provider_ticket":"   "}""")]
    public async Task A_blank_provider_ticket_is_a_string_and_so_403_reauth_failed(string body)   // criterion 24: provider_ticket is "a string"
    {
        var (userId, session) = await PrivateThroughProviderAsync("google", GooglePerson());

        using var response = await ProviderApi.DeleteAccountRaw(Client, session.AccessToken, body);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(1, await UsersAsync(userId));
        Assert.Equal("reauth_failed", AuditApi.Text(await SingleAsync(AuditKinds.AccountDeleteRefused), "reason"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_provider_ticket_with_a_live_binding_is_403_reauth_failed_and_the_live_ticket_stays(string blank)   // criterion 24
    {
        var (userId, session) = await PrivateThroughProviderAsync("google", GooglePerson());
        var reauth = await TicketAsync("google", GooglePerson(), "?purpose=reauth");

        using var response = await ProviderApi.DeleteAccount(Client, session.AccessToken, new { provider_ticket = blank }, reauth.Binding);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(1, await UsersAsync(userId));
        Assert.Equal(1, await TicketCountAsync());
        Assert.Equal("reauth_failed", AuditApi.Text(await SingleAsync(AuditKinds.AccountDeleteRefused), "reason"));
    }

    [Fact]
    public async Task A_provider_ticket_with_a_NUL_is_a_string_and_so_403_reauth_failed()   // criterion 24: only the password may not hold a NUL
    {
        var (userId, session) = await PrivateThroughProviderAsync("google", GooglePerson());

        using var response = await ProviderApi.DeleteAccountRaw(Client, session.AccessToken, "{\"provider_ticket\":\"ab" + Backslash + "u0000cd\"}");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(1, await UsersAsync(userId));
        Assert.Equal("reauth_failed", AuditApi.Text(await SingleAsync(AuditKinds.AccountDeleteRefused), "reason"));
    }

    [Fact]
    public async Task An_account_with_a_password_and_a_ticket_is_403_password_required_and_nothing_is_counted()   // criterion 24
    {
        var (userId, _) = await CreatePrivateAccountAsync(Ola);
        await LinkAsync(userId, "google", FakePeople.GoogleSubject);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        var reauth = await TicketAsync("google", GooglePerson(), "?purpose=reauth");

        using var response = await ProviderApi.DeleteAccount(Client, session.AccessToken, new { provider_ticket = reauth.Value }, reauth.Binding);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "password_required");
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(0, await StreaksAsync());
        Assert.Empty(await AuditAsync(AuditKinds.AccountDeleteRefused));
        Assert.Empty(await AuditAsync(AuditKinds.ProviderReauthenticated));
        Assert.Equal(1, await TicketCountAsync());
        Assert.Equal(1, await UsersAsync(userId));
    }

    [Fact]
    public async Task For_an_account_with_a_password_a_ticket_that_is_not_live_is_still_403_password_required()   // check order: the fit of the confirmation comes before the ticket
    {
        var (userId, _) = await CreatePrivateAccountAsync(Ola);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);

        using var response = await ProviderApi.DeleteAccount(Client, session.AccessToken, new { provider_ticket = Token43('Z') }, Token43('Y'));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "password_required");
        Assert.Equal(0, await StreaksAsync());
        Assert.Empty(await AuditAsync(AuditKinds.AccountDeleteRefused));
        Assert.Equal(1, await UsersAsync(userId));
    }

    [Fact]
    public async Task An_account_without_a_password_and_a_password_is_403_reauth_required_and_nothing_is_counted()   // criterion 24
    {
        var (userId, session) = await PrivateThroughProviderAsync("google", GooglePerson());

        using var response = await ProviderApi.DeleteAccount(Client, session.AccessToken, new { password = "Any-Passw0rd-1" }, null);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_required");
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(0, await StreaksAsync());
        Assert.Empty(await AuditAsync(AuditKinds.AccountDeleteRefused));
        Assert.Equal(1, await UsersAsync(userId));
    }

    [Fact]
    public async Task A_locked_identifier_does_not_stop_a_ticket_and_does_not_turn_reauth_required_into_429()   // check order: the fit comes before the lockout; the lockout is not consulted for a ticket
    {
        var (userId, session) = await PrivateThroughProviderAsync("google", GooglePerson());
        await LockoutApi.FailAsync(Client, Clock, Ola, 10);   // a password login for an account without a password counts as an unknown address does
        var streaks = await StreaksAsync();
        Assert.Equal(1, streaks);

        using var password = await ProviderApi.DeleteAccount(Client, session.AccessToken, new { password = "Any-Passw0rd-1" }, null);
        await TenancyApi.AssertErrorAsync(password, HttpStatusCode.Forbidden, "reauth_required");
        Assert.Equal(1, await StreaksAsync());
        Assert.Empty(await AuditAsync(AuditKinds.AccountDeleteRefused));

        var reauth = await TicketAsync("google", GooglePerson(), "?purpose=reauth");
        using var ticket = await ProviderApi.DeleteAccount(Client, session.AccessToken, new { provider_ticket = reauth.Value }, reauth.Binding);

        Assert.Equal(HttpStatusCode.NoContent, ticket.StatusCode);
        Assert.Equal(0, await UsersAsync(userId));
    }

    [Theory]
    [InlineData("sign_in")]
    [InlineData("invite")]
    [InlineData("expired")]
    [InlineData("used")]
    [InlineData("unknown")]
    [InlineData("no-binding")]
    [InlineData("another-browser")]
    [InlineData("another-account")]
    public async Task A_ticket_that_is_not_a_live_reauth_ticket_of_this_account_is_403_reauth_failed(string how)   // criterion 24
    {
        var (userId, session) = await PrivateThroughProviderAsync("google", GooglePerson());
        ProviderApi.Ticket ticket;
        switch (how)
        {
            case "sign_in":
                ticket = await TicketAsync("google", GooglePerson());
                break;
            case "invite":
                ticket = await TicketAsync("google", GooglePerson(), "?invite=" + Token43('A'));
                break;
            case "expired":
                ticket = await TicketAsync("google", GooglePerson(), "?purpose=reauth");
                Clock.Advance(TimeSpan.FromMinutes(5));
                break;
            case "used":
                // Another private person deletes their account with their own re-authentication; this account then shows that used ticket.
                var (_, other) = await PrivateThroughProviderAsync("microsoft", PersonalMicrosoft());
                ticket = await TicketAsync("microsoft", PersonalMicrosoft(), "?purpose=reauth");
                using (var deleted = await ProviderApi.DeleteAccount(Client, other.AccessToken, new { provider_ticket = ticket.Value }, ticket.Binding))
                {
                    Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
                }

                break;
            case "unknown":
                ticket = (await TicketAsync("google", GooglePerson(), "?purpose=reauth")) with { Value = Token43('Q') };
                break;
            case "another-browser":
                ticket = await TicketAsync("google", GooglePerson(), "?purpose=reauth");
                ticket = ticket with { Binding = (await TicketAsync("google", GooglePerson(), "?purpose=reauth")).Binding };
                break;
            case "another-account":
                await PrivateThroughProviderAsync("microsoft", PersonalMicrosoft());
                ticket = await TicketAsync("microsoft", PersonalMicrosoft(), "?purpose=reauth");   // an identity linked to the other account
                break;
            default:
                ticket = await TicketAsync("google", GooglePerson(), "?purpose=reauth");
                break;
        }

        var before = await TicketCountAsync();
        using var response = await ProviderApi.DeleteAccount(
            Client, session.AccessToken, new { provider_ticket = ticket.Value }, how == "no-binding" ? null : ticket.Binding);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.False(response.Headers.Contains("Set-Cookie"));   // nothing is cleared: the browser keeps its binding for the ticket that may still be live
        Assert.Equal(1, await UsersAsync(userId));
        Assert.Equal(before, await TicketCountAsync());   // the refusal rolled the deletion back: no ticket is used up by it
        var refusal = await SingleAsync(AuditKinds.AccountDeleteRefused);
        Assert.Equal("reauth_failed", AuditApi.Text(refusal, "reason"));
        Assert.Equal(userId, refusal.SubjectUserId);
        Assert.Equal(0, await StreaksAsync());   // the lockout is not consulted
        Assert.DoesNotContain(await AuditAsync(AuditKinds.ProviderReauthenticated), row => row.SubjectUserId == userId);   // the "used" case holds another person's deletion
        Assert.DoesNotContain(await AuditAsync(AuditKinds.AccountDeleted), row => row.SubjectUserId == userId);
    }

    [Fact]
    public async Task A_ticket_of_another_account_is_given_back_by_the_refusal_and_its_owner_then_deletes_with_it()   // rollback: a stranger cannot burn the owner's ticket
    {
        var (owner, ownerSession) = await PrivateThroughProviderAsync("google", GooglePerson());
        var (stranger, strangerSession) = await PrivateThroughProviderAsync("microsoft", PersonalMicrosoft());
        var reauth = await TicketAsync("google", GooglePerson(), "?purpose=reauth");

        using var refused = await ProviderApi.DeleteAccount(Client, strangerSession.AccessToken, new { provider_ticket = reauth.Value }, reauth.Binding);
        await TenancyApi.AssertErrorAsync(refused, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(1, await TicketCountAsync());
        Assert.Equal(1, await UsersAsync(stranger));

        using var accepted = await ProviderApi.DeleteAccount(Client, ownerSession.AccessToken, new { provider_ticket = reauth.Value }, reauth.Binding);

        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Equal(0, await UsersAsync(owner));
        Assert.Equal(1, await UsersAsync(stranger));
        Assert.Equal(0, await TicketCountAsync());
        Assert.Equal(stranger, (await SingleAsync(AuditKinds.AccountDeleteRefused)).SubjectUserId);
        Assert.Equal(owner, (await SingleAsync(AuditKinds.ProviderReauthenticated)).SubjectUserId);
    }

    [Fact]
    public async Task A_second_login_of_the_account_confirms_as_the_first_does()   // criterion 23: any login of this account
    {
        var (userId, session) = await PrivateThroughProviderAsync("google", GooglePerson());
        await LinkAsync(userId, "microsoft", PersonalKey);
        var reauth = await TicketAsync("microsoft", PersonalMicrosoft(), "?purpose=reauth");

        using var response = await ProviderApi.DeleteAccount(Client, session.AccessToken, new { provider_ticket = reauth.Value }, reauth.Binding);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await UsersAsync(userId));
    }

    [Fact]
    public async Task Two_deletions_with_one_ticket_delete_the_account_once_and_the_other_is_permissions_changed()   // race: exactly one wins
    {
        var (userId, session) = await PrivateThroughProviderAsync("google", GooglePerson());
        var reauth = await TicketAsync("google", GooglePerson(), "?purpose=reauth");

        Task<HttpResponseMessage> first, second;
        await using (var held = await HeldLock.AddressAsync(Factory, Normalized))
        {
            first = ProviderApi.DeleteAccount(Client, session.AccessToken, new { provider_ticket = reauth.Value }, reauth.Binding);
            second = ProviderApi.DeleteAccount(Client, session.AccessToken, new { provider_ticket = reauth.Value }, reauth.Binding);
            await HeldLock.WaitForWaitersAsync(Factory, 2, first, second);
            await held.ReleaseAsync();
        }

        using var a = await first;
        using var b = await second;

        var done = new[] { a, b }.Where(r => r.StatusCode == HttpStatusCode.NoContent).ToList();
        var other = new[] { a, b }.Where(r => r.StatusCode != HttpStatusCode.NoContent).ToList();
        Assert.Single(done);
        ProviderApi.AssertBindingCleared(done[0]);
        await TenancyApi.AssertErrorAsync(Assert.Single(other), HttpStatusCode.Forbidden, "permissions_changed");
        Assert.False(other[0].Headers.Contains("Set-Cookie"));
        Assert.Equal(0, await UsersAsync(userId));
        Assert.Equal(0, await TicketCountAsync());
        Assert.Single(await AuditAsync(AuditKinds.ProviderReauthenticated));
        Assert.Single(await AuditAsync(AuditKinds.AccountDeleted));
        Assert.Empty(await AuditAsync(AuditKinds.AccountDeleteRefused));
    }

    [Fact]
    public async Task A_ticket_that_expired_while_the_deletion_waited_for_the_address_lock_is_403_reauth_failed()   // clock after the locks
    {
        var (userId, session) = await PrivateThroughProviderAsync("google", GooglePerson());
        var reauth = await TicketAsync("google", GooglePerson(), "?purpose=reauth");

        using var response = await DeleteWhileLockedAsync(session.AccessToken, reauth, () =>
        {
            Clock.Advance(TimeSpan.FromMinutes(5));
            return Task.CompletedTask;
        });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(1, await UsersAsync(userId));
        Assert.Equal(1, await TicketCountAsync());
        Assert.Equal("reauth_failed", AuditApi.Text(await SingleAsync(AuditKinds.AccountDeleteRefused), "reason"));
        Assert.Empty(await AuditAsync(AuditKinds.ProviderReauthenticated));
    }

    [Fact]
    public async Task A_ticket_still_live_when_the_lock_is_released_deletes_the_account()   // the twin of the test above: the wait alone refuses nothing
    {
        var (userId, session) = await PrivateThroughProviderAsync("google", GooglePerson());
        var reauth = await TicketAsync("google", GooglePerson(), "?purpose=reauth");

        using var response = await DeleteWhileLockedAsync(session.AccessToken, reauth, () =>
        {
            Clock.Advance(TimeSpan.FromMinutes(4));
            return Task.CompletedTask;
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await UsersAsync(userId));
    }

    [Fact]
    public async Task A_ticket_used_up_while_the_deletion_waited_for_the_address_lock_is_403_reauth_failed()   // race window
    {
        var (userId, session) = await PrivateThroughProviderAsync("google", GooglePerson());
        var reauth = await TicketAsync("google", GooglePerson(), "?purpose=reauth");

        using var response = await DeleteWhileLockedAsync(
            session.AccessToken, reauth, () => InDbAsync(db => db.ProviderTickets.ExecuteDeleteAsync(TestContext.Current.CancellationToken)));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(1, await UsersAsync(userId));
        Assert.Equal("reauth_failed", AuditApi.Text(await SingleAsync(AuditKinds.AccountDeleteRefused), "reason"));
    }

    [Fact]
    public async Task An_identity_unlinked_while_the_deletion_waited_for_the_address_lock_is_403_reauth_failed_and_the_ticket_stays()   // race window
    {
        var (userId, session) = await PrivateThroughProviderAsync("google", GooglePerson());
        await LinkAsync(userId, "microsoft", PersonalKey);   // the account keeps a login, so only the ticket's identity goes
        var reauth = await TicketAsync("google", GooglePerson(), "?purpose=reauth");

        using var response = await DeleteWhileLockedAsync(
            session.AccessToken,
            reauth,
            () => InDbAsync(db => db.UserLogins.Where(l => l.LoginProvider == "google").ExecuteDeleteAsync(TestContext.Current.CancellationToken)));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(1, await UsersAsync(userId));
        Assert.Equal(1, await TicketCountAsync());
        Assert.Equal("reauth_failed", AuditApi.Text(await SingleAsync(AuditKinds.AccountDeleteRefused), "reason"));
        Assert.Empty(await AuditAsync(AuditKinds.ProviderReauthenticated));
    }

    [Fact]
    public async Task A_password_set_while_the_deletion_waited_for_the_address_lock_makes_the_ticket_403_password_required()   // race window: the account is read under the lock
    {
        var (userId, session) = await PrivateThroughProviderAsync("google", GooglePerson());
        var reauth = await TicketAsync("google", GooglePerson(), "?purpose=reauth");
        var hash = new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "Chosen-Passw0rd");

        using var response = await DeleteWhileLockedAsync(
            session.AccessToken,
            reauth,
            () => InDbAsync(db => db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(
                s => s.SetProperty(u => u.PasswordHash, hash), TestContext.Current.CancellationToken)));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "password_required");
        Assert.Equal(1, await UsersAsync(userId));
        Assert.Equal(1, await TicketCountAsync());
        Assert.Equal(0, await StreaksAsync());
        Assert.Empty(await AuditAsync(AuditKinds.AccountDeleteRefused));
        Assert.Empty(await AuditAsync(AuditKinds.ProviderReauthenticated));
    }

    [Fact]
    public async Task A_password_removed_while_the_deletion_waited_for_the_address_lock_makes_the_password_403_reauth_required()   // race window
    {
        var (userId, _) = await CreatePrivateAccountAsync(Ola);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        Task<HttpResponseMessage> pending;

        await using (var held = await HeldLock.AddressAsync(Factory, Normalized))
        {
            pending = ProviderApi.DeleteAccount(Client, session.AccessToken, new { password = UserPassword }, null);
            await HeldLock.WaitForWaitersAsync(Factory, 1, pending);
            await InDbAsync(db => db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(
                s => s.SetProperty(u => u.PasswordHash, (string?)null), TestContext.Current.CancellationToken));
            await held.ReleaseAsync();
        }

        using var response = await pending;
        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_required");
        Assert.Equal(1, await UsersAsync(userId));
        Assert.Equal(0, await StreaksAsync());
        Assert.Empty(await AuditAsync(AuditKinds.AccountDeleteRefused));
    }

    [Fact]
    public async Task An_account_deleted_while_the_deletion_waited_for_the_address_lock_is_permissions_changed_and_the_ticket_stays()   // race window
    {
        var (userId, session) = await PrivateThroughProviderAsync("google", GooglePerson());
        var reauth = await TicketAsync("google", GooglePerson(), "?purpose=reauth");

        using var response = await DeleteWhileLockedAsync(
            session.AccessToken, reauth, () => InDbAsync(db => db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(TestContext.Current.CancellationToken)));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "permissions_changed");
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await AuditAsync(AuditKinds.AccountDeleteRefused));
        Assert.Empty(await AuditAsync(AuditKinds.ProviderReauthenticated));
    }

    [Fact]
    public async Task A_failure_of_the_deletion_after_the_ticket_is_used_up_leaves_the_account_the_ticket_and_the_cookies_as_they_were()   // rollback of the whole deletion
    {
        var (userId, session) = await PrivateThroughProviderAsync("google", GooglePerson());
        var reauth = await TicketAsync("google", GooglePerson(), "?purpose=reauth");
        _fault.Armed = true;

        using var failed = await ProviderApi.DeleteAccount(Client, session.AccessToken, new { provider_ticket = reauth.Value }, reauth.Binding);

        Assert.True(_fault.Fired, "The deletion never reached the delete of the account.");
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal("""{"error":"internal_error"}""", await failed.Content.ReadAsStringAsync());
        Assert.False(failed.Headers.Contains("Set-Cookie"));   // neither auth_rt nor auth_pt is cleared
        Assert.Equal(1, await UsersAsync(userId));
        Assert.Equal(1, await InDbAsync(db => db.UserLogins.CountAsync(l => l.UserId == userId, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await TicketCountAsync());   // the ticket is unused, still there
        Assert.Empty(await AuditAsync(AuditKinds.ProviderReauthenticated));
        Assert.Empty(await AuditAsync(AuditKinds.AccountDeleted));
        Assert.Empty(await AuditAsync(AuditKinds.AccountDeleteRefused));
        using var stillValid = await SessionApi.Refresh(Client, session.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, stillValid.StatusCode);

        _fault.Armed = false;
        using var again = await ProviderApi.DeleteAccount(Client, session.AccessToken, new { provider_ticket = reauth.Value }, reauth.Binding);

        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);   // the same ticket, now that nothing fails
        Assert.Equal(0, await UsersAsync(userId));
        Assert.Single(await AuditAsync(AuditKinds.ProviderReauthenticated));
    }

    [Fact]
    public async Task No_ticket_binding_password_or_session_token_reaches_the_logs_the_audit_rows_or_an_error_body()   // no secret
    {
        var (_, session) = await PrivateThroughProviderAsync("google", GooglePerson());
        await PrivateThroughProviderAsync("microsoft", PersonalMicrosoft());
        var foreign = await TicketAsync("microsoft", PersonalMicrosoft(), "?purpose=reauth");
        var own = await TicketAsync("google", GooglePerson(), "?purpose=reauth");
        const string typed = "Typed-Passw0rd-9";

        using var refused = await ProviderApi.DeleteAccount(Client, session.AccessToken, new { provider_ticket = foreign.Value }, foreign.Binding);
        using var wrongKind = await ProviderApi.DeleteAccount(Client, session.AccessToken, new { password = typed }, null);
        using var deleted = await ProviderApi.DeleteAccount(Client, session.AccessToken, new { provider_ticket = own.Value }, own.Binding);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongKind.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        string[] secrets = [foreign.Value, foreign.Binding, own.Value, own.Binding, typed, session.AccessToken, session.RefreshToken];
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, Logs.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, await wrongKind.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        await AssertNoSecretsAsync(secrets);
    }

    /// <summary>Throws just before the account delete is sent while armed: the deletion fails after the ticket was used up.</summary>
    private sealed class DeleteFaultInterceptor : DbCommandInterceptor
    {
        public bool Armed { get; set; }

        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Fail(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Fail(command);
            return ValueTask.FromResult(result);
        }

        private void Fail(DbCommand command)
        {
            if (Armed && command.CommandText.Contains("DELETE FROM \"AspNetUsers\"", StringComparison.Ordinal))
            {
                Fired = true;
                throw new InvalidOperationException("the deletion fails for the test");
            }
        }
    }
}
