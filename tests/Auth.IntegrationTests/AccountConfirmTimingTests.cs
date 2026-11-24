using System.Net;
using Auth.FakeOidc;
using Auth.Infrastructure.Identity;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Lockout;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0012 → <c>POST /auth/account/confirm</c>, steps 3 and 4: the caller is known, so there is no decoy to run; a password is verified once,
/// against the stored hash, and only when the confirmation fits the account and the identifier is not locked. Every other answer verifies nothing.
/// </summary>
public sealed class AccountConfirmTimingTests : ProviderTestBase
{
    private const string Ola = FakePeople.GoogleEmail;

    private readonly CountingPasswordHasher _hasher = new();

    public AccountConfirmTimingTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IPasswordHasher<ApplicationUser>>(_hasher)));
    }

    private async Task<string> StoredHashAsync(string email)
    {
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return (await users.FindByEmailAsync(email))!.PasswordHash!;
    }

    private async Task<SessionApi.Session> PasswordSessionAsync()
    {
        _ = await CreateUserAsync(Ola, confirmed: true);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        Clock.Advance(TimeSpan.FromMinutes(6));
        return session;
    }

    [Fact]
    public async Task A_wrong_password_runs_exactly_one_verification_against_the_stored_hash()
    {
        var session = await PasswordSessionAsync();
        var before = _hasher.VerifiedHashes.Count;

        using var response = await SettingsApi.Confirm(Client, session, new { password = LockoutApi.WrongPassword });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal([await StoredHashAsync(Ola)], _hasher.VerifiedHashes.Skip(before).ToArray());
    }

    [Fact]
    public async Task The_right_password_runs_exactly_one_verification_against_the_stored_hash()
    {
        var session = await PasswordSessionAsync();
        var before = _hasher.VerifiedHashes.Count;

        using var response = await SettingsApi.Confirm(Client, session, new { password = UserPassword });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal([await StoredHashAsync(Ola)], _hasher.VerifiedHashes.Skip(before).ToArray());
    }

    [Fact]
    public async Task A_locked_attempt_runs_no_verification_at_all()
    {
        var session = await PasswordSessionAsync();
        for (var i = 0; i < 10; i++)
        {
            Clock.Advance(LockoutApi.HumanPace);
            using var failed = await SettingsApi.Confirm(Client, session, new { password = LockoutApi.WrongPassword });
            Assert.Equal(HttpStatusCode.Forbidden, failed.StatusCode);
        }

        Clock.Advance(LockoutApi.HumanPace);
        var before = _hasher.VerifiedHashes.Count;

        using var locked = await SettingsApi.Confirm(Client, session, new { password = UserPassword });

        await LockoutApi.AssertLockedAsync(locked);
        Assert.Equal(before, _hasher.VerifiedHashes.Count);
    }

    [Fact]
    public async Task A_ticket_for_an_account_with_a_password_a_bad_body_and_a_missing_session_run_no_verification()
    {
        var session = await PasswordSessionAsync();
        var ticket = await TicketAsync("google", GooglePerson(), "?purpose=reauth");
        var before = _hasher.VerifiedHashes.Count;

        using var ticketForPassword = await SettingsApi.ConfirmWithTicket(Client, session, ticket);
        using var badBody = await SettingsApi.PostRaw(Client, SettingsApi.ConfirmPath, session, "{}");
        using var noCookie = await SettingsApi.PostAs(Client, SettingsApi.ConfirmPath, session.AccessToken, null, new { password = UserPassword });

        await TenancyApi.AssertErrorAsync(ticketForPassword, HttpStatusCode.Forbidden, "password_required");
        await TenancyApi.AssertErrorAsync(badBody, HttpStatusCode.BadRequest, "invalid_request");
        await TenancyApi.AssertErrorAsync(noCookie, HttpStatusCode.Unauthorized, "session_required");
        Assert.Equal(before, _hasher.VerifiedHashes.Count);
    }

    [Fact]
    public async Task A_password_for_an_account_without_one_and_a_provider_confirmation_run_no_verification()
    {
        var (_, session) = await PrivateThroughProviderAsync("google", GooglePerson());
        Clock.Advance(TimeSpan.FromMinutes(6));
        var ticket = await TicketAsync("google", GooglePerson(), "?purpose=reauth");
        var before = _hasher.VerifiedHashes.Count;

        using var passwordForPasswordless = await SettingsApi.Confirm(Client, session, new { password = UserPassword });
        using var confirmed = await SettingsApi.ConfirmWithTicket(Client, session, ticket);

        await TenancyApi.AssertErrorAsync(passwordForPasswordless, HttpStatusCode.Forbidden, "reauth_required");
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        Assert.Equal(before, _hasher.VerifiedHashes.Count);
    }
}
