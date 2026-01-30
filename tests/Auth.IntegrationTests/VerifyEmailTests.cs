using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

public sealed class VerifyEmailTests(PostgresFixture postgres, KeyMaterialFixture keys) : MailTestBase(postgres, keys)
{
    private const string Email = "new@example.com";
    private static readonly TimeSpan PastTheLimit = TimeSpan.FromSeconds(61);

    /// <summary>Asks for a verification mail for <see cref="Email"/>, lets the dispatcher send it and returns its token.</summary>
    private async Task<string> VerificationTokenAsync()
    {
        using var response = await AccountApi.RequestVerification(Client, Email);
        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        await DispatchAsync();
        return TokenIn(Mail.Sent[^1]);
    }

    private Task<bool> ConfirmedAsync() =>
        InDbAsync(db => db.Users.Where(u => u.Email == Email).Select(u => u.EmailConfirmed).SingleAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task Verification_confirms_the_email()   // criterion 12
    {
        await CreateUserAsync(Email, confirmed: false);
        var token = await VerificationTokenAsync();

        using var response = await AccountApi.Verify(Client, token);

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
        Assert.True(await ConfirmedAsync());
        Assert.Equal(0, await InDbAsync(db => db.EmailTokens.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Verification_leaves_the_password_and_a_reset_link_alone()
    {
        await CreateUserAsync(Email, confirmed: false);
        var token = await VerificationTokenAsync();
        using (var forgot = await AccountApi.Forgot(Client, Email))
        {
            await AccountApi.AssertEmptyAsync(forgot, HttpStatusCode.Accepted);
        }

        await DispatchAsync();
        var reset = TokenIn(Mail.Sent[^1]);

        using var response = await AccountApi.Verify(Client, token);
        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);

        // The reset link of the same account still works.
        using var resetResponse = await AccountApi.Reset(Client, reset, "Brand-New-Passw0rd");
        await AccountApi.AssertEmptyAsync(resetResponse, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Unknown_used_expired_and_replaced_tokens_get_one_answer()   // criterion 6
    {
        await CreateUserAsync(Email, confirmed: false);
        var replaced = await VerificationTokenAsync();
        Clock.Advance(PastTheLimit);
        var expired = await VerificationTokenAsync();
        // Asked now, while `replaced` is a minute old: it is refused for being replaced, not for its age.
        using var replacedResponse = await AccountApi.Verify(Client, replaced);
        Clock.Advance(EmailTokens.VerificationLifetime);
        var used = await VerificationTokenAsync();
        using (var ok = await AccountApi.Verify(Client, used))
        {
            await AccountApi.AssertEmptyAsync(ok, HttpStatusCode.NoContent);
        }

        using var unknownResponse = await AccountApi.Verify(Client, "never-issued");
        using var usedResponse = await AccountApi.Verify(Client, used);
        using var expiredResponse = await AccountApi.Verify(Client, expired);

        foreach (var response in new[] { unknownResponse, usedResponse, replacedResponse, expiredResponse })
        {
            await AccountApi.AssertErrorAsync(response, AccountApi.InvalidToken);
            Assert.Equal(LoginApi.HeaderNames(unknownResponse), LoginApi.HeaderNames(response));
        }
    }

    [Fact]
    public async Task Expired_token_confirms_nothing()   // criterion 6
    {
        await CreateUserAsync(Email, confirmed: false);
        var token = await VerificationTokenAsync();
        Clock.Advance(EmailTokens.VerificationLifetime);

        using var response = await AccountApi.Verify(Client, token);

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidToken);
        Assert.False(await ConfirmedAsync());
    }

    [Fact]
    public async Task Token_is_good_until_just_under_24_hours()   // criterion 6
    {
        await CreateUserAsync(Email, confirmed: false);
        var token = await VerificationTokenAsync();
        Clock.Advance(EmailTokens.VerificationLifetime - TimeSpan.FromSeconds(1));

        using var response = await AccountApi.Verify(Client, token);

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Reset_token_does_not_verify_and_stays_usable()
    {
        await CreateUserAsync(Email, confirmed: false);
        using (var forgot = await AccountApi.Forgot(Client, Email))
        {
            await AccountApi.AssertEmptyAsync(forgot, HttpStatusCode.Accepted);
        }

        await DispatchAsync();
        var reset = TokenIn(Assert.Single(Mail.Sent));

        using var response = await AccountApi.Verify(Client, reset);

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidToken);
        Assert.False(await ConfirmedAsync());
        Assert.Equal(1, await InDbAsync(db => db.EmailTokens.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Parallel_verifications_with_one_token_succeed_once()   // Review Focus 1
    {
        await CreateUserAsync(Email, confirmed: false);
        var token = await VerificationTokenAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => AccountApi.Verify(Client, token))));

        try
        {
            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.NoContent));
            foreach (var response in responses.Where(r => r.StatusCode != HttpStatusCode.NoContent))
            {
                await AccountApi.AssertErrorAsync(response, AccountApi.InvalidToken);
            }

            Assert.True(await ConfirmedAsync());
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Confirmed_account_gets_no_further_verification_mail()   // criterion 11
    {
        await CreateUserAsync(Email, confirmed: false);
        var token = await VerificationTokenAsync();
        using (var verified = await AccountApi.Verify(Client, token))
        {
            await AccountApi.AssertEmptyAsync(verified, HttpStatusCode.NoContent);
        }

        Clock.Advance(PastTheLimit);
        using var again = await AccountApi.RequestVerification(Client, Email);
        await AccountApi.AssertEmptyAsync(again, HttpStatusCode.Accepted);
        await DispatchAsync();

        Assert.Single(Mail.Sent);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"token":""}""")]
    [InlineData("""{"token":123}""")]
    [InlineData("""["x"]""")]
    [InlineData("not json")]
    public async Task Malformed_request_is_a_400(string body)
    {
        using var response = await AccountApi.PostRaw(Client, AccountApi.VerifyPath, body);

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidRequest);
    }
}
