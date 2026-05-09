using System.Net;
using Auth.FakeOidc;
using Auth.Infrastructure.Identity;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0012 → <c>POST /auth/account/password</c>, steps 3 to 5: the caller is known, so there is no decoy to run; the current password is verified
/// once, against the stored hash, and only when the body, the session, the account's password and the policy let it get that far and the identifier
/// is not locked. Every other answer verifies nothing, and nothing is verified twice (a verification that rehashed would write the row the change
/// is about to update).
/// </summary>
public sealed class AccountPasswordTimingTests : ProviderTestBase
{
    private const string Ola = FakePeople.GoogleEmail;
    private const string NewPassword = "Fresh-Passw0rd-2";

    private readonly CountingPasswordHasher _hasher = new();

    public AccountPasswordTimingTests(PostgresFixture postgres, KeyMaterialFixture keys)
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
        return await SessionApi.LoginAsync(Client, Ola, UserPassword);
    }

    [Fact]
    public async Task A_wrong_current_password_runs_exactly_one_verification_against_the_stored_hash()
    {
        var session = await PasswordSessionAsync();
        var stored = await StoredHashAsync(Ola);
        var before = _hasher.VerifiedHashes.Count;

        using var response = await SettingsApi.ChangePassword(Client, session, LockoutApi.WrongPassword, NewPassword);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal([stored], _hasher.VerifiedHashes.Skip(before).ToArray());
    }

    [Fact]
    public async Task The_right_current_password_runs_exactly_one_verification_against_the_stored_hash()
    {
        var session = await PasswordSessionAsync();
        var stored = await StoredHashAsync(Ola);
        var before = _hasher.VerifiedHashes.Count;

        using var response = await SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([stored], _hasher.VerifiedHashes.Skip(before).ToArray());
    }

    [Fact]
    public async Task A_locked_attempt_runs_no_verification_at_all()
    {
        var session = await PasswordSessionAsync();
        await LockoutApi.FailAsync(Client, Clock, Ola, 10);
        Clock.Advance(LockoutApi.HumanPace);
        var before = _hasher.VerifiedHashes.Count;

        using var locked = await SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword);

        await LockoutApi.AssertLockedAsync(locked);
        Assert.Equal(before, _hasher.VerifiedHashes.Count);
    }

    [Fact]
    public async Task A_weak_new_password_a_bad_body_and_a_missing_session_run_no_verification()
    {
        var session = await PasswordSessionAsync();
        var before = _hasher.VerifiedHashes.Count;

        using var weak = await SettingsApi.ChangePassword(Client, session, UserPassword, "short");
        using var badBody = await SettingsApi.PostRaw(Client, SettingsApi.PasswordPath, session, "{}");
        using var noCookie = await SettingsApi.PostAs(
            Client, SettingsApi.PasswordPath, session.AccessToken, null, new { current_password = UserPassword, new_password = NewPassword });

        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        await TenancyApi.AssertErrorAsync(badBody, HttpStatusCode.BadRequest, "invalid_request");
        await TenancyApi.AssertErrorAsync(noCookie, HttpStatusCode.Unauthorized, "session_required");
        Assert.Equal(before, _hasher.VerifiedHashes.Count);
    }

    [Fact]
    public async Task An_account_without_a_password_runs_no_verification()
    {
        var (_, session) = await PrivateThroughProviderAsync("google", GooglePerson());
        var before = _hasher.VerifiedHashes.Count;

        using var response = await SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "password_not_set");
        Assert.Equal(before, _hasher.VerifiedHashes.Count);
    }
}
