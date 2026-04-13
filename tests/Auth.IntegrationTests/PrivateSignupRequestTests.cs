using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Lockout;
using Auth.Server.Sessions;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → <c>POST /auth/signup</c> with both switches on.</summary>
public sealed class PrivateSignupRequestTests : SignupTestBase
{
    private const string Private = "ola@poczta.test";
    private const string Backslash = "\\";

    public PrivateSignupRequestTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithPrivateSignup();

        // As in SignupRequestTests: no background pass of the host may log statements of its own while a test compares what requests ran.
        Factory.WithoutHostedService<TokenPruningService>().WithoutHostedService<LockoutPruningService>();
    }

    private Task<HttpResponseMessage> PrivateRequestAsync(string email = Private) =>
        AccountApi.PostRaw(Client, SignupApi.RequestPath, $$"""{"email":"{{email}}","kind":"private"}""");

    /// <summary>The SQL of every database command the host has logged since <paramref name="mark"/> log entries.</summary>
    private List<string> StatementsSince(int mark) =>
        [.. Logs.Entries.Skip(mark)
            .Where(e => e.Category == "Microsoft.EntityFrameworkCore.Database.Command")
            .Select(e => e.Message[(e.Message.IndexOf('\n', StringComparison.Ordinal) + 1)..].Trim())];

    [Fact]
    public async Task A_private_request_is_accepted_with_an_empty_body_and_stores_its_kind_and_no_company()   // criterion 4
    {
        using var response = await PrivateRequestAsync("Ola@Poczta.test");

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        var pending = Assert.Single(await PendingAsync());
        Assert.Equal(SignupKind.Private, pending.Kind);
        Assert.Null(pending.CompanyName);
        Assert.Equal("Ola@Poczta.test", pending.Email);
        Assert.Equal(1, await QueueCountAsync());
        var row = await SingleAsync(AuditKinds.SignupRequested);
        Assert.Equal("private", AuditApi.Text(row, "kind"));
        Assert.Equal(["kind"], AuditApi.DetailNames(row));
        Assert.Equal("Ola@Poczta.test", row.SubjectEmail);
    }

    [Fact]
    public async Task A_private_request_ignores_members_it_does_not_know()   // spec 0010, general rule: unknown members are ignored
    {
        using var response = await AccountApi.PostRaw(
            Client, SignupApi.RequestPath, """{"email":"ola@poczta.test","kind":"private","extra":{"a":1},"other":[1]}""");

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        Assert.Equal(SignupKind.Private, Assert.Single(await PendingAsync()).Kind);
    }

    [Fact]
    public async Task A_request_without_kind_is_an_organization_sign_up_as_before()   // criterion 4
    {
        using var response = await SignupApi.Request(Client, Address, CompanyName);

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        var pending = Assert.Single(await PendingAsync());
        Assert.Equal(SignupKind.Organization, pending.Kind);
        Assert.Equal(CompanyName, pending.CompanyName);
        Assert.Equal("organization", AuditApi.Text(await SingleAsync(AuditKinds.SignupRequested), "kind"));
    }

    [Theory]
    [InlineData("""{"email":"ola@poczta.test","kind":"Private"}""")]
    [InlineData("""{"email":"ola@poczta.test","kind":"company"}""")]
    [InlineData("""{"email":"ola@poczta.test","kind":""}""")]
    [InlineData("""{"email":"ola@poczta.test","kind":null}""")]
    [InlineData("""{"email":"ola@poczta.test","kind":2}""")]
    [InlineData("""{"email":"ola@poczta.test","kind":"private","company_name":"Firma"}""")]
    [InlineData("""{"email":"ola@poczta.test","kind":"private","company_name":null}""")]
    [InlineData("""{"email":"ola@poczta.test","kind":"private","company_name":""}""")]
    [InlineData("""{"email":"ola@poczta.test","kind":"organization"}""")]
    [InlineData("""{"email":"ola@poczta.test"}""")]
    [InlineData("""{"kind":"private"}""")]
    [InlineData("""{"email":"not-an-address","kind":"private"}""")]
    [InlineData("{\"" + Backslash + "ud800\":1,\"email\":\"ola@poczta.test\",\"kind\":\"private\"}")]   // a member name with an unpaired surrogate escape
    [InlineData("{\"email\":\"ola@poczta.test\",\"kind\":\"private\",\"" + Backslash + "ud800\":1}")]
    public async Task Unknown_kinds_a_private_company_name_and_an_organization_without_one_are_400_and_queue_nothing(string body)   // criterion 4
    {
        using var response = await AccountApi.PostRaw(Client, SignupApi.RequestPath, body);

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidRequest);
        Assert.Empty(await PendingAsync());
        Assert.Equal(0, await QueueCountAsync());
        Assert.Equal(0, await LimitCountAsync());
        Assert.Empty(await AuditAsync(AuditKinds.SignupRequested));
    }

    [Fact]
    public async Task The_mail_limit_counts_both_kinds_together()   // criterion 6
    {
        using (var organization = await SignupApi.Request(Client, Private, CompanyName))
        {
            Assert.Equal(HttpStatusCode.Accepted, organization.StatusCode);
        }

        using (var refused = await PrivateRequestAsync())
        {
            Assert.True(await LockoutApi.AssertLockedAsync(refused) > 0);   // 429 too_many_attempts with retry_after_seconds
        }

        Clock.Advance(PastTheLimit);
        using var later = await PrivateRequestAsync();
        Assert.Equal(HttpStatusCode.Accepted, later.StatusCode);
    }

    [Fact]
    public async Task A_request_that_changes_the_kind_voids_the_earlier_link_at_once()   // criterion 7
    {
        var link = await MailedAsync(Private);   // an organization link, mailed
        Clock.Advance(PastTheLimit);

        using (var changed = await PrivateRequestAsync())
        {
            Assert.Equal(HttpStatusCode.Accepted, changed.StatusCode);
        }

        using var preview = await SignupApi.Preview(Client, link);   // before any new mail is sent
        await AccountApi.AssertErrorAsync(preview, AccountApi.InvalidToken);
    }

    [Fact]
    public async Task A_request_that_changes_only_the_company_name_keeps_the_link_until_the_new_mail()   // criterion 7
    {
        var link = await MailedAsync(Private, "First");
        Clock.Advance(PastTheLimit);

        using (var renamed = await SignupApi.Request(Client, Private, "Second"))
        {
            Assert.Equal(HttpStatusCode.Accepted, renamed.StatusCode);
        }

        using var preview = await SignupApi.Preview(Client, link);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
    }

    [Fact]
    public async Task A_private_request_runs_the_same_statements_and_answers_the_same_for_any_address()   // criterion 5
    {
        await CreatePrivateAccountAsync("held@poczta.test");

        var mark = Logs.Entries.Count;
        using var company = await PrivateRequestAsync(Factory.SeedEmail);   // an account that belongs to a company
        var forCompany = StatementsSince(mark);

        mark = Logs.Entries.Count;
        using var held = await PrivateRequestAsync("held@poczta.test");     // a private account
        var forPrivate = StatementsSince(mark);

        mark = Logs.Entries.Count;
        using var unknown = await PrivateRequestAsync("nobody@poczta.test");
        var forUnknown = StatementsSince(mark);

        foreach (var response in new[] { company, held, unknown })
        {
            await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
            Assert.Equal(LoginApi.HeaderNames(company), LoginApi.HeaderNames(response));
        }

        Assert.NotEmpty(forUnknown);
        Assert.Equal(forUnknown, forCompany);
        Assert.Equal(forUnknown, forPrivate);
        Assert.DoesNotContain(forCompany.Concat(forPrivate).Concat(forUnknown), sql => sql.Contains("AspNetUsers", StringComparison.Ordinal));
        Assert.DoesNotContain(forCompany.Concat(forPrivate).Concat(forUnknown), sql => sql.Contains("Memberships", StringComparison.Ordinal));
    }
}
