using System.Net;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → Accounts without a password: "forgot password" sets one (criterion 25, Decision 15).</summary>
public sealed class ProviderPasswordResetTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Ola = FakePeople.GoogleEmail;
    private const string Chosen = "Chosen-Passw0rd";

    private async Task<string> ResetTokenAsync(string email)
    {
        using var forgot = await AccountApi.Forgot(Client, email);
        await AccountApi.AssertEmptyAsync(forgot, HttpStatusCode.Accepted);
        await DispatchAsync();
        return TokenIn(Mail.Sent.Last(m => m.To == email));
    }

    [Fact]
    public async Task Forgot_password_sets_a_password_for_an_account_without_one_and_both_then_sign_in()   // criterion 25
    {
        var (userId, _) = await PrivateThroughProviderAsync("google", GooglePerson());

        using (var forgot = await AccountApi.Forgot(Client, Ola))
        {
            await AccountApi.AssertEmptyAsync(forgot, HttpStatusCode.Accepted);
        }

        await DispatchAsync();
        using (var reset = await AccountApi.Reset(Client, TokenIn(Mail.Sent.Last(m => m.To == Ola)), Chosen))
        {
            await AccountApi.AssertEmptyAsync(reset, HttpStatusCode.NoContent);
        }

        var password = await SessionApi.LoginAsync(Client, Ola, Chosen);
        Assert.Equal(userId.ToString(), AccessTokens.Text(password.AccessToken, "sub"));
        var ticket = await TicketAsync("google", GooglePerson());
        using (var exchanged = await ProviderApi.Exchange(Client, ticket))
        {
            Assert.Equal(userId.ToString(), AccessTokens.Text((await SessionApi.ReadSessionAsync(exchanged)).AccessToken, "sub"));
        }

        using var me = await TenancyApi.Get(Client, "/auth/me", password.AccessToken);
        var body = await TenancyApi.ReadOkAsync(me);
        Assert.True(body.GetProperty("has_password").GetBoolean());
        Assert.Equal(["google"], body.GetProperty("linked_providers").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task The_answer_and_the_mail_for_an_account_without_a_password_are_those_of_an_account_with_one()   // no enumeration difference
    {
        await PrivateThroughProviderAsync("google", GooglePerson());
        await CreateUserAsync("withpassword@gmail.test", confirmed: true);

        using var passwordless = await AccountApi.Forgot(Client, Ola);
        using var withPassword = await AccountApi.Forgot(Client, "withpassword@gmail.test");
        using var unknown = await AccountApi.Forgot(Client, "nobody@gmail.test");

        foreach (var response in new[] { passwordless, withPassword, unknown })
        {
            await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        }

        static string Shape(HttpResponseMessage r) => string.Join(
            '\n',
            r.Headers.Concat(r.Content.Headers)
                .Where(h => h.Key is not "Date")
                .OrderBy(h => h.Key, StringComparer.Ordinal)
                .Select(h => h.Key + "=" + string.Join(',', h.Value)));
        Assert.Equal(Shape(withPassword), Shape(passwordless));
        Assert.Equal(Shape(withPassword), Shape(unknown));
        await DispatchAsync();
        var toPasswordless = Assert.Single(Mail.Sent, m => m.To == Ola);
        var toWithPassword = Assert.Single(Mail.Sent, m => m.To == "withpassword@gmail.test");
        Assert.Equal(toWithPassword.Subject, toPasswordless.Subject);   // no new mail and no changed text
        Assert.DoesNotContain(Mail.Sent, m => m.To == "nobody@gmail.test");
    }

    [Fact]
    public async Task The_reset_ends_the_sessions_and_clears_the_streak_of_an_account_that_had_no_password()   // spec 0011 → Accounts without a password: the reset keeps its effects of spec 0004
    {
        var (_, session) = await PrivateThroughProviderAsync("google", GooglePerson());
        await LockoutApi.FailAsync(Client, Clock, Ola, 3);
        Assert.Equal(1, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));
        var token = await ResetTokenAsync(Ola);

        using (var reset = await AccountApi.Reset(Client, token, Chosen))
        {
            await AccountApi.AssertEmptyAsync(reset, HttpStatusCode.NoContent);
        }

        using var refresh = await SessionApi.Refresh(Client, session.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(refresh);
        Assert.Equal(0, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_reset_for_an_account_without_a_password_keeps_its_logins_and_stores_a_hash()   // spec 0011: the account then has a password and its logins
    {
        var (userId, _) = await PrivateThroughProviderAsync("google", GooglePerson());
        var token = await ResetTokenAsync(Ola);

        using (var reset = await AccountApi.Reset(Client, token, Chosen))
        {
            await AccountApi.AssertEmptyAsync(reset, HttpStatusCode.NoContent);
        }

        Assert.Equal(1, await InDbAsync(db => db.UserLogins.CountAsync(l => l.UserId == userId, TestContext.Current.CancellationToken)));
        Assert.NotNull(await InDbAsync(db => db.Users.Where(u => u.Id == userId).Select(u => u.PasswordHash).SingleAsync(TestContext.Current.CancellationToken)));
    }
}
