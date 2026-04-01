using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;

namespace Auth.IntegrationTests;

public sealed class SignupPreviewTests(PostgresFixture postgres, KeyMaterialFixture keys) : SignupTestBase(postgres, keys)
{
    public static TheoryData<string> MalformedBodies => new()
    {
        "{}",
        """{"token":""}""",
        """{"token":"   "}""",
        """{"token":123}""",
        """{"token":null}""",
        """["token"]""",
        "not json",
        "",
    };

    [Fact]
    public async Task Preview_answers_the_typed_address_and_the_company_and_changes_nothing()   // criterion 8
    {
        var token = await MailedAsync("Anna@Firma.test", "Firma & Syn");
        var before = Assert.Single(await PendingAsync());
        var auditBefore = (await AuditAsync()).Count;

        for (var i = 0; i < 3; i++)
        {
            using var response = await SignupApi.Preview(Client, token);

            var body = await TenancyApi.ReadOkAsync(response);
            Assert.Equal(["company_name", "email"], body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.Equal("Anna@Firma.test", body.GetProperty("email").GetString());
            Assert.Equal("Firma & Syn", body.GetProperty("company_name").GetString());
        }

        var after = Assert.Single(await PendingAsync());
        Assert.Equal(before.TokenHash, after.TokenHash);
        Assert.Equal(before.TokenExpiresAt, after.TokenExpiresAt);
        Assert.Equal(before.ExpiresAt, after.ExpiresAt);
        Assert.Equal(auditBefore, (await AuditAsync()).Count);
        Assert.Equal(0, await QueueCountAsync());
    }

    [Fact]
    public async Task Unknown_and_expired_tokens_are_one_400_invalid_token()   // criterion 8
    {
        var token = await MailedAsync();
        using var unknown = await SignupApi.Preview(Client, EmailTokens.NewToken());
        await AccountApi.AssertErrorAsync(unknown, AccountApi.InvalidToken);

        Clock.Advance(TimeSpan.FromHours(24));
        using var expired = await SignupApi.Preview(Client, token);
        await AccountApi.AssertErrorAsync(expired, AccountApi.InvalidToken);
    }

    [Fact]
    public async Task A_link_works_until_24_hours_after_the_mail_and_not_a_second_longer()   // criterion 13
    {
        var token = await MailedAsync();

        Clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        using (var last = await SignupApi.Preview(Client, token))
        {
            Assert.Equal(HttpStatusCode.OK, last.StatusCode);
        }

        Clock.Advance(TimeSpan.FromSeconds(1));
        using var over = await SignupApi.Preview(Client, token);
        await AccountApi.AssertErrorAsync(over, AccountApi.InvalidToken);
    }

    [Fact]
    public async Task The_older_link_of_an_address_is_invalid_and_the_newer_shows_the_newer_company()   // criterion 7, Review Focus 3
    {
        var first = await MailedAsync(company: "First");
        Clock.Advance(PastTheLimit);
        var second = await MailedAsync(company: "Second");

        using var older = await SignupApi.Preview(Client, first);
        await AccountApi.AssertErrorAsync(older, AccountApi.InvalidToken);

        using var newer = await SignupApi.Preview(Client, second);
        var body = await TenancyApi.ReadOkAsync(newer);
        Assert.Equal("Second", body.GetProperty("company_name").GetString());
    }

    [Fact]
    public async Task A_link_of_another_kind_is_not_a_sign_up_link()
    {
        using var forgot = await AccountApi.Forgot(Client, Factory.SeedEmail);
        Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);
        await DispatchAsync();
        var resetToken = TokenIn(Assert.Single(Mail.Sent));

        using var response = await SignupApi.Preview(Client, resetToken);

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidToken);
    }

    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public async Task Malformed_bodies_are_400_invalid_request(string body)
    {
        using var response = await AccountApi.PostRaw(Client, SignupApi.PreviewPath, body);

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidRequest);
    }
}
