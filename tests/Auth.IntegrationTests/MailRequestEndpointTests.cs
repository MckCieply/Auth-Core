using System.Net;
using System.Text.RegularExpressions;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

public sealed class MailRequestEndpointTests(PostgresFixture postgres, KeyMaterialFixture keys) : MailTestBase(postgres, keys)
{
    private const string Unknown = "nobody@example.com";

    private static readonly string[] MalformedBodies =
    [
        "{}",
        """{"email":""}""",
        """{"email":"   "}""",
        """{"email":123}""",
        """{"email":null}""",
        """["a@example.com"]""",
        "not json",
        "",
        """{"email":"a@example.com",""",
        """{"email":"a\u0000b@example.com"}""",              // NUL
        """{"email":"a@example.com\n"}""",                   // control character
        """{"email":"a\uFFFEb@example.com"}""",              // the noncharacter of escalation E1
        """{"email":"a\uD800b@example.com"}""",              // unpaired surrogate escape
        $$"""{"email":"{{new string('a', 243)}}@example.com"}""",   // 255 characters
    ];

    private Task<HttpResponseMessage> Request(MailKind kind, string email) =>
        kind == MailKind.PasswordReset ? AccountApi.Forgot(Client, email) : AccountApi.RequestVerification(Client, email);

    private Task<int> QueueCountAsync() =>
        InDbAsync(db => db.MailRequests.CountAsync(TestContext.Current.CancellationToken));

    private Task<int> LimitCountAsync() =>
        InDbAsync(db => db.MailRequestLimits.CountAsync(TestContext.Current.CancellationToken));

    /// <summary>The SQL of every database command the host has logged since <paramref name="mark"/> log entries.</summary>
    private List<string> StatementsSince(int mark) =>
        [.. Logs.Entries.Skip(mark)
            .Where(e => e.Category == "Microsoft.EntityFrameworkCore.Database.Command")
            .Select(e => e.Message[(e.Message.IndexOf('\n', StringComparison.Ordinal) + 1)..].Trim())];

    [Fact]
    public async Task Forgot_for_an_account_is_accepted_and_a_reset_mail_follows()   // criterion 1
    {
        using var response = await AccountApi.Forgot(Client, Factory.SeedEmail);

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        Assert.Empty(Mail.Sent);                               // nothing is sent inside the request
        await DispatchAsync();
        var mail = Assert.Single(Mail.Sent);
        Assert.Equal(Factory.SeedEmail, mail.To);
        Assert.Matches(Regex.Escape(AuthAppFactory.DefaultResetUrl) + @"\?token=[A-Za-z0-9_-]{43}\s", mail.TextBody);
    }

    [Fact]
    public async Task Address_without_an_account_gets_the_same_answer_the_same_rows_and_no_mail()   // criterion 2
    {
        using var known = await AccountApi.Forgot(Client, Factory.SeedEmail);
        using var unknown = await AccountApi.Forgot(Client, Unknown);

        await AccountApi.AssertEmptyAsync(unknown, HttpStatusCode.Accepted);
        Assert.Equal(known.StatusCode, unknown.StatusCode);
        Assert.Equal(LoginApi.HeaderNames(known), LoginApi.HeaderNames(unknown));

        // The request did the same work for both: one limit row and one queue row each, alike but for the address.
        var rows = await InDbAsync(db => db.MailRequests.AsNoTracking().OrderBy(r => r.Id).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, rows.Count);
        Assert.Equal(
            (rows[0].Kind, rows[0].Attempts, rows[0].RequestedAt, rows[0].NextAttemptAt),
            (rows[1].Kind, rows[1].Attempts, rows[1].RequestedAt, rows[1].NextAttemptAt));
        Assert.Equal(2, await LimitCountAsync());

        await DispatchAsync();
        Assert.Equal(Factory.SeedEmail, Assert.Single(Mail.Sent).To);
        Assert.Equal(0, await QueueCountAsync());
    }

    [Theory]
    [InlineData(MailKind.PasswordReset)]
    [InlineData(MailKind.EmailVerification)]
    public async Task Request_runs_the_same_statements_for_any_address_and_never_reads_the_accounts(MailKind kind)   // criterion 2
    {
        var mark = Logs.Entries.Count;
        using var known = await Request(kind, Factory.SeedEmail);
        var forKnown = StatementsSince(mark);

        mark = Logs.Entries.Count;
        using var unknown = await Request(kind, Unknown);
        var forUnknown = StatementsSince(mark);

        // A pruning pass of the host may log a DELETE of its own in between; what the request runs is not one.
        static List<string> OfTheRequest(List<string> statements) =>
            [.. statements.Where(sql => sql.Contains("MailRequest", StringComparison.Ordinal) && !sql.StartsWith("DELETE", StringComparison.Ordinal))];

        Assert.NotEmpty(OfTheRequest(forKnown));
        Assert.Equal(OfTheRequest(forKnown), OfTheRequest(forUnknown));
        Assert.DoesNotContain(forKnown.Concat(forUnknown), sql => sql.Contains("AspNetUsers", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(MailKind.PasswordReset)]
    [InlineData(MailKind.EmailVerification)]
    public async Task Second_request_within_a_minute_is_refused_and_asking_again_does_not_add_to_the_wait(MailKind kind)   // criterion 9
    {
        using var first = await Request(kind, Factory.SeedEmail);
        await AccountApi.AssertEmptyAsync(first, HttpStatusCode.Accepted);

        using var second = await Request(kind, Factory.SeedEmail);
        Assert.Equal(60, await LockoutApi.AssertLockedAsync(second));

        Clock.Advance(TimeSpan.FromSeconds(20));
        using var third = await Request(kind, Factory.SeedEmail);
        Assert.Equal(40, await LockoutApi.AssertLockedAsync(third));

        Clock.Advance(TimeSpan.FromSeconds(40));
        using var fourth = await Request(kind, Factory.SeedEmail);
        await AccountApi.AssertEmptyAsync(fourth, HttpStatusCode.Accepted);
        Assert.Equal(2, await QueueCountAsync());
    }

    [Fact]
    public async Task Sixth_request_within_the_hour_is_refused_until_the_hour_is_over()   // criterion 9
    {
        for (var i = 0; i < 5; i++)
        {
            using var accepted = await AccountApi.Forgot(Client, Factory.SeedEmail);
            await AccountApi.AssertEmptyAsync(accepted, HttpStatusCode.Accepted);
            Clock.Advance(TimeSpan.FromSeconds(61));
        }

        using var sixth = await AccountApi.Forgot(Client, Factory.SeedEmail);
        Assert.Equal(3600 - 305, await LockoutApi.AssertLockedAsync(sixth));

        Clock.Advance(TimeSpan.FromSeconds(3600 - 305));
        using var later = await AccountApi.Forgot(Client, Factory.SeedEmail);
        await AccountApi.AssertEmptyAsync(later, HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Limit_answers_the_same_for_an_address_without_an_account()   // criterion 10
    {
        using var knownFirst = await AccountApi.Forgot(Client, Factory.SeedEmail);
        using var known = await AccountApi.Forgot(Client, Factory.SeedEmail);
        using var unknownFirst = await AccountApi.Forgot(Client, Unknown);
        using var unknown = await AccountApi.Forgot(Client, Unknown);

        Assert.Equal(await LockoutApi.AssertLockedAsync(known), await LockoutApi.AssertLockedAsync(unknown));
        Assert.Equal(LoginApi.HeaderNames(known), LoginApi.HeaderNames(unknown));
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Kinds_do_not_share_a_limit()   // criterion 9
    {
        using var forgot = await AccountApi.Forgot(Client, Factory.SeedEmail);
        using var verification = await AccountApi.RequestVerification(Client, Factory.SeedEmail);
        using var forgotAgain = await AccountApi.Forgot(Client, Factory.SeedEmail);

        await AccountApi.AssertEmptyAsync(forgot, HttpStatusCode.Accepted);
        await AccountApi.AssertEmptyAsync(verification, HttpStatusCode.Accepted);
        await LockoutApi.AssertLockedAsync(forgotAgain);
    }

    [Fact]
    public async Task Spelling_variants_of_one_address_share_the_limit_and_reach_the_account()   // Review Focus 2
    {
        using var upper = await AccountApi.Forgot(Client, Factory.SeedEmail.ToUpperInvariant());
        using var mixed = await AccountApi.Forgot(Client, "User@Example.Com");

        await AccountApi.AssertEmptyAsync(upper, HttpStatusCode.Accepted);
        await LockoutApi.AssertLockedAsync(mixed);
        await DispatchAsync();
        Assert.Equal(Factory.SeedEmail, Assert.Single(Mail.Sent).To);
    }

    [Fact]
    public async Task Verification_mail_is_sent_only_to_an_unconfirmed_account()   // criterion 11
    {
        await CreateUserAsync("new@example.com", confirmed: false);

        using var unconfirmed = await AccountApi.RequestVerification(Client, "new@example.com");
        using var confirmed = await AccountApi.RequestVerification(Client, Factory.SeedEmail);
        using var unknown = await AccountApi.RequestVerification(Client, Unknown);

        foreach (var response in new[] { unconfirmed, confirmed, unknown })
        {
            await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
            Assert.Equal(LoginApi.HeaderNames(unconfirmed), LoginApi.HeaderNames(response));
        }

        await DispatchAsync();
        var mail = Assert.Single(Mail.Sent);
        Assert.Equal("new@example.com", mail.To);
        Assert.Matches(Regex.Escape(AuthAppFactory.DefaultVerifyUrl) + @"\?token=[A-Za-z0-9_-]{43}\s", mail.TextBody);
    }

    [Theory]
    [InlineData(AccountApi.ForgotPath)]
    [InlineData(AccountApi.VerifyRequestPath)]
    public async Task Malformed_request_is_a_400_and_is_not_counted(string path)   // criterion 16
    {
        // One host for all of them: a host per body would start a database per body.
        foreach (var body in MalformedBodies)
        {
            using var response = await AccountApi.PostRaw(Client, path, body);

            await AccountApi.AssertErrorAsync(response, AccountApi.InvalidRequest);
        }

        Assert.Equal(0, await LimitCountAsync());
        Assert.Equal(0, await QueueCountAsync());
    }

    [Theory]
    [InlineData(AccountApi.ForgotPath)]
    [InlineData(AccountApi.VerifyRequestPath)]
    public async Task Body_that_is_not_json_or_too_large_is_a_400(string path)
    {
        using var text = await AccountApi.PostRaw(Client, path, """{"email":"a@example.com"}""", "text/plain");
        using var large = await AccountApi.PostRaw(Client, path, $$"""{"email":"a@example.com","pad":"{{new string('x', 9000)}}"}""");

        await AccountApi.AssertErrorAsync(text, AccountApi.InvalidRequest);
        await AccountApi.AssertErrorAsync(large, AccountApi.InvalidRequest);
    }

    [Fact]
    public async Task Address_of_254_characters_is_accepted()
    {
        using var response = await AccountApi.Forgot(Client, new string('a', 242) + "@example.com");

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
    }

    [Theory]
    [InlineData(AccountApi.ForgotPath)]
    [InlineData(AccountApi.VerifyRequestPath)]
    public async Task Only_post_is_allowed(string path)
    {
        using var response = await Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
