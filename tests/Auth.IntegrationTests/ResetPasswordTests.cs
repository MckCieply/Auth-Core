using System.Net;
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace Auth.IntegrationTests;

public sealed class ResetPasswordTests(PostgresFixture postgres, KeyMaterialFixture keys) : MailTestBase(postgres, keys)
{
    private const string NewPassword = "Brand-New-Passw0rd";
    private static readonly TimeSpan PastTheLimit = TimeSpan.FromSeconds(61);

    /// <summary>Asks for a reset mail for <paramref name="email"/>, lets the dispatcher send it and returns its token.</summary>
    private async Task<string> ResetTokenAsync(string email)
    {
        using var response = await AccountApi.Forgot(Client, email);
        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        await DispatchAsync();
        return TokenIn(Mail.Sent[^1]);
    }

    private async Task<HttpStatusCode> LoginStatusAsync(string email, string password)
    {
        // Past the burst window of the lockout, so a test's own failed logins never add up to a lock.
        Clock.Advance(LockoutApi.HumanPace);
        using var response = await LoginApi.Login(Client, email, password);
        return response.StatusCode;
    }

    [Fact]
    public async Task Reset_replaces_the_password()   // criterion 3
    {
        var token = await ResetTokenAsync(Factory.SeedEmail);

        using var response = await AccountApi.Reset(Client, token, NewPassword);

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginStatusAsync(Factory.SeedEmail, Factory.SeedPassword));
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(Factory.SeedEmail, NewPassword));
    }

    [Fact]
    public async Task Reset_ends_every_session_of_the_account()   // criterion 4
    {
        var first = await SessionApi.LoginAsync(Client, Factory);
        Clock.Advance(TimeSpan.FromSeconds(30));
        var rotated = await SessionApi.RefreshOk(Client, first.RefreshToken);
        var second = await SessionApi.LoginAsync(Client, Factory);
        await CreateUserAsync("other@example.com", confirmed: true);
        using var otherLogin = await LoginApi.Login(Client, "other@example.com", UserPassword);
        var other = SessionApi.RefreshCookieOf(otherLogin).Value.Value!;
        var token = await ResetTokenAsync(Factory.SeedEmail);

        using var response = await AccountApi.Reset(Client, token, NewPassword);
        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);

        foreach (var refreshToken in new[] { rotated.RefreshToken, second.RefreshToken })
        {
            using var refresh = await SessionApi.Refresh(Client, refreshToken);
            await SessionApi.AssertInvalidGrantAsync(refresh);
        }

        // Someone else's session is untouched.
        _ = await SessionApi.RefreshOk(Client, other);

        // A login with the new password starts a session that works.
        Clock.Advance(LockoutApi.HumanPace);
        using var login = await LoginApi.Login(Client, Factory.SeedEmail, NewPassword);
        var cookie = SessionApi.RefreshCookieOf(login);
        _ = await SessionApi.RefreshOk(Client, cookie.Value.Value!);
    }

    [Fact]
    public async Task Reset_revokes_every_token_and_authorization_of_the_account()   // criterion 4
    {
        // Read from the store, so the revocation is proved apart from the security-stamp check, which would
        // refuse these sessions even if nothing were revoked.
        var first = await SessionApi.LoginAsync(Client, Factory);
        Clock.Advance(TimeSpan.FromSeconds(30));
        _ = await SessionApi.RefreshOk(Client, first.RefreshToken);   // leaves a redeemed token behind
        _ = await SessionApi.LoginAsync(Client, Factory);
        var token = await ResetTokenAsync(Factory.SeedEmail);

        using var response = await AccountApi.Reset(Client, token, NewPassword);
        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);

        var subject = (await Factory.SeedUserIdAsync()).ToString();
        using var scope = Factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();

        var found = await tokens.FindBySubjectAsync(subject, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(found);
        foreach (var item in found)
        {
            Assert.Equal(OpenIddictConstants.Statuses.Revoked, await tokens.GetStatusAsync(item, TestContext.Current.CancellationToken));
        }

        var grants = await authorizations.FindBySubjectAsync(subject, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(grants);
        foreach (var item in grants)
        {
            Assert.Equal(OpenIddictConstants.Statuses.Revoked, await authorizations.GetStatusAsync(item, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Session_whose_login_overlapped_a_password_change_cannot_refresh()   // criterion 4
    {
        // What such a login leaves behind: a session that no revocation saw, begun on the old security stamp.
        var session = await SessionApi.LoginAsync(Client, Factory);
        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByEmailAsync(Factory.SeedEmail))!;
            Assert.True((await users.UpdateSecurityStampAsync(user)).Succeeded);
        }

        using var refresh = await SessionApi.Refresh(Client, session.RefreshToken);

        await SessionApi.AssertInvalidGrantAsync(refresh);
    }

    [Fact]
    public async Task Reset_lifts_a_lockout_and_confirms_the_email_in_one_go()   // criterion 5, Review Focus 5
    {
        const string Email = "locked@example.com";
        await CreateUserAsync(Email, confirmed: false);
        await LockoutApi.FailAsync(Client, Clock, Email, 10);
        using (var locked = await LoginApi.Login(Client, Email, UserPassword))
        {
            await LockoutApi.AssertLockedAsync(locked);
        }

        var token = await ResetTokenAsync(Email);
        using var response = await AccountApi.Reset(Client, token, NewPassword);
        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);

        // No waiting: the cooldown is gone, and (from Task 10 on) the unconfirmed-email refusal with it.
        using var login = await LoginApi.Login(Client, Email, NewPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True(await InDbAsync(db => db.Users.Where(u => u.Email == Email).Select(u => u.EmailConfirmed).SingleAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(0, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Reset_makes_the_other_links_of_the_account_unusable()   // criterion 5
    {
        const string Email = "new@example.com";
        await CreateUserAsync(Email, confirmed: false);
        using (var verification = await AccountApi.RequestVerification(Client, Email))
        {
            await AccountApi.AssertEmptyAsync(verification, HttpStatusCode.Accepted);
        }

        // One pass sends both mails, the verification first; the token returned is the reset's.
        var token = await ResetTokenAsync(Email);
        Assert.Equal(2, await InDbAsync(db => db.EmailTokens.CountAsync(TestContext.Current.CancellationToken)));

        using var response = await AccountApi.Reset(Client, token, NewPassword);
        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);

        Assert.Equal(0, await InDbAsync(db => db.EmailTokens.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Token_works_once()   // criterion 6
    {
        var token = await ResetTokenAsync(Factory.SeedEmail);
        using var first = await AccountApi.Reset(Client, token, NewPassword);
        await AccountApi.AssertEmptyAsync(first, HttpStatusCode.NoContent);

        using var second = await AccountApi.Reset(Client, token, "Yet-Another-Passw0rd");

        await AccountApi.AssertErrorAsync(second, AccountApi.InvalidToken);
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(Factory.SeedEmail, NewPassword));
    }

    [Fact]
    public async Task Unknown_used_expired_and_replaced_tokens_get_one_answer()   // criterion 6
    {
        var used = await ResetTokenAsync(Factory.SeedEmail);
        using (var ok = await AccountApi.Reset(Client, used, NewPassword))
        {
            await AccountApi.AssertEmptyAsync(ok, HttpStatusCode.NoContent);
        }

        Clock.Advance(PastTheLimit);
        var replaced = await ResetTokenAsync(Factory.SeedEmail);
        Clock.Advance(PastTheLimit);
        var expired = await ResetTokenAsync(Factory.SeedEmail);
        // Asked now, while `replaced` is a minute old: it is refused for being replaced, not for its age.
        using var replacedResponse = await AccountApi.Reset(Client, replaced, NewPassword);
        Clock.Advance(EmailTokens.ResetLifetime);

        using var unknownResponse = await AccountApi.Reset(Client, "never-issued", NewPassword);
        using var usedResponse = await AccountApi.Reset(Client, used, NewPassword);
        using var expiredResponse = await AccountApi.Reset(Client, expired, NewPassword);

        foreach (var response in new[] { unknownResponse, usedResponse, replacedResponse, expiredResponse })
        {
            await AccountApi.AssertErrorAsync(response, AccountApi.InvalidToken);
            Assert.Equal(LoginApi.HeaderNames(unknownResponse), LoginApi.HeaderNames(response));
        }
    }

    [Fact]
    public async Task Unusable_token_is_reported_before_a_weak_password()
    {
        using var response = await AccountApi.Reset(Client, "never-issued", "abc");

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidToken);
    }

    [Fact]
    public async Task Reset_does_not_clear_the_mail_limit()
    {
        var token = await ResetTokenAsync(Factory.SeedEmail);
        using (var reset = await AccountApi.Reset(Client, token, NewPassword))
        {
            await AccountApi.AssertEmptyAsync(reset, HttpStatusCode.NoContent);
        }

        using var again = await AccountApi.Forgot(Client, Factory.SeedEmail);

        Assert.Equal(60, await LockoutApi.AssertLockedAsync(again));
    }

    [Fact]
    public async Task Token_is_good_until_just_under_an_hour()   // criterion 6
    {
        var token = await ResetTokenAsync(Factory.SeedEmail);
        Clock.Advance(EmailTokens.ResetLifetime - TimeSpan.FromSeconds(1));

        using var response = await AccountApi.Reset(Client, token, NewPassword);

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Verification_token_does_not_reset_a_password_and_stays_usable()
    {
        const string Email = "new@example.com";
        await CreateUserAsync(Email, confirmed: false);
        using (var requested = await AccountApi.RequestVerification(Client, Email))
        {
            await AccountApi.AssertEmptyAsync(requested, HttpStatusCode.Accepted);
        }

        await DispatchAsync();
        var verification = TokenIn(Assert.Single(Mail.Sent));

        using var response = await AccountApi.Reset(Client, verification, NewPassword);

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidToken);
        Assert.Equal(1, await InDbAsync(db => db.EmailTokens.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("abc", """["too_short","requires_upper","requires_digit"]""")]
    [InlineData("abcdefgh", """["requires_upper","requires_digit"]""")]
    [InlineData("ABCDEFG1", """["requires_lower"]""")]
    [InlineData("Abcdefgh", """["requires_digit"]""")]
    [InlineData("Ab1", """["too_short"]""")]
    [InlineData("1234567", """["too_short","requires_upper","requires_lower"]""")]
    public async Task Weak_password_names_every_broken_rule_and_leaves_everything_as_it_was(string password, string rules)   // criterion 7
    {
        var session = await SessionApi.LoginAsync(Client, Factory);
        var token = await ResetTokenAsync(Factory.SeedEmail);

        using var weak = await AccountApi.Reset(Client, token, password);

        await AccountApi.AssertErrorAsync(weak, $$"""{"error":"weak_password","rules":{{rules}}}""");
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(Factory.SeedEmail, Factory.SeedPassword));
        _ = await SessionApi.RefreshOk(Client, session.RefreshToken);

        // The same token still works with an acceptable password.
        using var strong = await AccountApi.Reset(Client, token, NewPassword);
        await AccountApi.AssertEmptyAsync(strong, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Password_without_a_special_character_is_accepted()   // criterion 8
    {
        var token = await ResetTokenAsync(Factory.SeedEmail);

        using var response = await AccountApi.Reset(Client, token, "Abcdefg1");

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(Factory.SeedEmail, "Abcdefg1"));
    }

    [Fact]
    public async Task Letters_of_any_script_satisfy_the_policy()   // criterion 8
    {
        const string Polish = "zażółć12Ż";
        var token = await ResetTokenAsync(Factory.SeedEmail);

        using var response = await AccountApi.Reset(Client, token, Polish);

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(Factory.SeedEmail, Polish));
    }

    [Fact]
    public async Task New_password_may_equal_the_old_one()
    {
        var token = await ResetTokenAsync(Factory.SeedEmail);

        using var response = await AccountApi.Reset(Client, token, Factory.SeedPassword);

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Parallel_resets_with_one_token_change_the_password_once()   // Review Focus 1
    {
        var token = await ResetTokenAsync(Factory.SeedEmail);
        var passwords = Enumerable.Range(0, 6).Select(i => $"Parallel-Passw0rd-{i}").ToArray();

        var responses = await Task.WhenAll(passwords.Select(p => Task.Run(() => AccountApi.Reset(Client, token, p))));

        try
        {
            var winner = Assert.Single(Enumerable.Range(0, responses.Length), i => responses[i].StatusCode == HttpStatusCode.NoContent);
            for (var i = 0; i < responses.Length; i++)
            {
                if (i != winner)
                {
                    await AccountApi.AssertErrorAsync(responses[i], AccountApi.InvalidToken);
                }
            }

            Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(Factory.SeedEmail, passwords[winner]));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"token":"x"}""")]
    [InlineData("""{"new_password":"Brand-New-Passw0rd"}""")]
    [InlineData("""{"token":"","new_password":"Brand-New-Passw0rd"}""")]
    [InlineData("""{"token":"x","new_password":"   "}""")]
    [InlineData("""{"token":"x","new_password":123}""")]
    [InlineData("""{"token":"x","new_password":"Brand-New\u0000Passw0rd"}""")]   // NUL, as a JSON escape
    [InlineData("not json")]
    public async Task Malformed_request_is_a_400(string body)
    {
        using var response = await AccountApi.PostRaw(Client, AccountApi.ResetPath, body);

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidRequest);
    }

    [Fact]
    public async Task Malformed_request_with_a_good_token_does_not_use_it_up()
    {
        var token = await ResetTokenAsync(Factory.SeedEmail);

        using var malformed = await AccountApi.PostRaw(Client, AccountApi.ResetPath, $$"""{"token":"{{token}}","new_password":123}""");
        await AccountApi.AssertErrorAsync(malformed, AccountApi.InvalidRequest);

        using var response = await AccountApi.Reset(Client, token, NewPassword);
        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
    }
}
