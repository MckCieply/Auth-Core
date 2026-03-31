using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class SignupRequestTests(PostgresFixture postgres, KeyMaterialFixture keys) : SignupTestBase(postgres, keys)
{
    // Spelled out, so that the escapes written into a request body are not read as escapes of this file.
    private const string Backslash = "\\";

    public static TheoryData<string> MalformedBodies => new()
    {
        "{}",
        """{"email":"anna@firma.test"}""",
        """{"company_name":"Firma"}""",
        """{"email":"","company_name":"Firma"}""",
        """{"email":"   ","company_name":"Firma"}""",
        """{"email":"anna@firma.test","company_name":""}""",
        """{"email":"anna@firma.test","company_name":"   "}""",
        """{"email":123,"company_name":"Firma"}""",
        """{"email":"anna@firma.test","company_name":null}""",
        """{"email":"anna@firma.test","company_name":["Firma"]}""",
        """["anna@firma.test","Firma"]""",
        "not json",
        "",
        """{"email":"anna@firma.test","company_name":"Firma",""",
        "{\"email\":\"anna@firma.test\",\"company_name\":\"Fi" + Backslash + "u0000rma\"}",   // NUL
        "{\"email\":\"anna@firma.test\",\"company_name\":\"Fi" + Backslash + "ud800rma\"}",   // an unpaired surrogate escape
        "{\"email\":\"anna@firma" + Backslash + "u0000.test\",\"company_name\":\"Firma\"}",
    };

    public static TheoryData<string> RefusedAddresses => new()
    {
        "bob.firma.test",
        " anna@firma.test",
        "anna@firma.test ",
        "Anna <anna@firma.test>",
        "anna@firma.test, bob@firma.test",
        "anna@localhost",
        "anna@intranet",
        "anna@[10.0.0.5]",
        "anna@10.0.0.5",
        "anna@127.0x1",
        "anna@host.123",
        new string('a', 244) + "@firma.test",                                  // 255 characters
        char.ConvertFromUtf32(0x17F) + "nna@firma.test",                       // the long s: stands for another address
        "a" + (char)0xFFFE + "b@firma.test",                                   // a noncharacter
    };

    public static TheoryData<string> RefusedCompanyNames => new()
    {
        " Firma",
        "Firma ",
        new string('x', 101),
        "Fi" + (char)1 + "rma",
        "Fi" + (char)10 + "rma",
        "Fi" + char.ConvertFromUtf32(0x200B) + "rma",                          // a format character
        "Fi" + (char)0xFFFE + "rma",
    };

    /// <summary>The SQL of every database command the host has logged since <paramref name="mark"/> log entries.</summary>
    private List<string> StatementsSince(int mark) =>
        [.. Logs.Entries.Skip(mark)
            .Where(e => e.Category == "Microsoft.EntityFrameworkCore.Database.Command")
            .Select(e => e.Message[(e.Message.IndexOf('\n', StringComparison.Ordinal) + 1)..].Trim())];

    private async Task AssertNothingWasMadeAsync()
    {
        Assert.Empty(await PendingAsync());
        Assert.Equal(0, await QueueCountAsync());
        Assert.Equal(0, await LimitCountAsync());
        Assert.Empty(await AuditAsync(AuditKinds.SignupRequested));
    }

    [Fact]
    public async Task A_new_address_is_accepted_with_an_empty_body_and_a_pending_sign_up_is_stored()   // criterion 3
    {
        using var response = await SignupApi.Request(Client, "Anna@Firma.test", CompanyName);

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        Assert.Empty(Mail.Sent);   // nothing is sent inside the request
        var pending = Assert.Single(await PendingAsync());
        Assert.Equal("ANNA@FIRMA.TEST", pending.NormalizedEmail);
        Assert.Equal("Anna@Firma.test", pending.Email);
        Assert.Equal(CompanyName, pending.CompanyName);
        Assert.Equal(Clock.GetUtcNow().AddHours(24).UtcDateTime, pending.ExpiresAt.UtcDateTime, TimeSpan.FromMilliseconds(1));
        Assert.Null(pending.TokenHash);
        var queued = await InDbAsync(db => db.MailRequests.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(MailKind.Signup, queued.Kind);
        Assert.Equal("ANNA@FIRMA.TEST", queued.NormalizedEmail);
        Assert.Null(queued.InviteId);
    }

    [Fact]
    public async Task An_address_with_an_account_gets_the_same_status_headers_and_body()   // criterion 4
    {
        using var known = await SignupApi.Request(Client, Factory.SeedEmail, CompanyName);
        using var unknown = await SignupApi.Request(Client, "nobody@firma.test", CompanyName);

        await AccountApi.AssertEmptyAsync(known, HttpStatusCode.Accepted);
        await AccountApi.AssertEmptyAsync(unknown, HttpStatusCode.Accepted);
        Assert.Equal(LoginApi.HeaderNames(known), LoginApi.HeaderNames(unknown));
        // The same work for both: one pending sign-up and one queued request each, alike but for the address.
        var rows = await InDbAsync(db => db.MailRequests.AsNoTracking().OrderBy(r => r.Id).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, rows.Count);
        Assert.Equal(
            (rows[0].Kind, rows[0].Attempts, rows[0].RequestedAt, rows[0].NextAttemptAt),
            (rows[1].Kind, rows[1].Attempts, rows[1].RequestedAt, rows[1].NextAttemptAt));
        Assert.Equal(2, (await PendingAsync()).Count);
        Assert.Equal(2, await LimitCountAsync());
    }

    [Fact]
    public async Task The_request_runs_the_same_statements_for_any_address_and_never_reads_the_accounts()   // criterion 4
    {
        var mark = Logs.Entries.Count;
        using var known = await SignupApi.Request(Client, Factory.SeedEmail, CompanyName);
        var forKnown = StatementsSince(mark);

        mark = Logs.Entries.Count;
        using var unknown = await SignupApi.Request(Client, "nobody@firma.test", CompanyName);
        var forUnknown = StatementsSince(mark);

        // A pass of the host may log a DELETE of its own in between; what the request runs is not one.
        static List<string> OfTheRequest(List<string> statements) =>
            [.. statements.Where(sql => !sql.StartsWith("DELETE", StringComparison.Ordinal)
                && (sql.Contains("PendingSignups", StringComparison.Ordinal)
                    || sql.Contains("MailRequest", StringComparison.Ordinal)
                    || sql.Contains("audit_events", StringComparison.Ordinal)))];

        Assert.NotEmpty(OfTheRequest(forKnown));
        Assert.Contains(forKnown, sql => sql.Contains("PendingSignups", StringComparison.Ordinal));
        Assert.Equal(OfTheRequest(forKnown), OfTheRequest(forUnknown));
        Assert.DoesNotContain(forKnown.Concat(forUnknown), sql => sql.Contains("AspNetUsers", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public async Task Malformed_bodies_are_400_invalid_request_and_queue_nothing(string body)   // criterion 5
    {
        using var response = await AccountApi.PostRaw(Client, SignupApi.RequestPath, body);

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidRequest);
        await AssertNothingWasMadeAsync();
    }

    [Fact]
    public async Task A_body_that_is_not_json_by_its_content_type_is_400_invalid_request()
    {
        using var response = await AccountApi.PostRaw(
            Client, SignupApi.RequestPath, """{"email":"anna@firma.test","company_name":"Firma"}""", "text/plain");

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidRequest);
        await AssertNothingWasMadeAsync();
    }

    [Theory]
    [MemberData(nameof(RefusedAddresses))]
    public async Task Addresses_the_invitation_rule_refuses_are_400_and_queue_nothing(string address)   // criterion 5
    {
        using var response = await SignupApi.Request(Client, address, CompanyName);

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidRequest);
        await AssertNothingWasMadeAsync();
    }

    [Theory]
    [MemberData(nameof(RefusedCompanyNames))]
    public async Task Company_names_that_break_the_name_rule_are_400_and_queue_nothing(string name)   // criterion 5
    {
        using var response = await SignupApi.Request(Client, Address, name);

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidRequest);
        await AssertNothingWasMadeAsync();
    }

    [Fact]
    public async Task Names_at_the_limit_with_polish_letters_and_pairs_are_kept_whole()   // Review Focus 4 (the half that reaches the mail is in SignupDispatchTests)
    {
        var factory = char.ConvertFromUtf32(0x1F3ED);
        string[] names =
        [
            new string('Ż', 100),
            "Żółw i Spółka",
            string.Concat(Enumerable.Repeat(factory, 50)),   // 100 UTF-16 characters in 50 pairs
            factory + " Fabryka & <Syn>",
        ];

        for (var i = 0; i < names.Length; i++)
        {
            var address = $"anna{i}@firma.test";
            using var response = await SignupApi.Request(Client, address, names[i]);
            await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        }

        var stored = (await PendingAsync()).ToDictionary(p => p.Email, p => p.CompanyName);
        for (var i = 0; i < names.Length; i++)
        {
            Assert.Equal(names[i], stored[$"anna{i}@firma.test"]);
        }
    }

    [Fact]
    public async Task Spellings_of_one_address_share_one_pending_sign_up_and_one_limit()   // Review Focus 2
    {
        var firstAt = Clock.GetUtcNow();
        using var first = await SignupApi.Request(Client, "Anna@Firma.test", "First");
        await AccountApi.AssertEmptyAsync(first, HttpStatusCode.Accepted);

        using var second = await SignupApi.Request(Client, "ANNA@firma.TEST", "Refused");
        Assert.Equal(60, await LockoutApi.AssertLockedAsync(second));   // the same address to the limit

        // The refused request changed nothing: the row is still the first request's.
        var kept = Assert.Single(await PendingAsync());
        Assert.Equal("Anna@Firma.test", kept.Email);
        Assert.Equal("First", kept.CompanyName);
        Assert.Equal(firstAt.AddHours(24).UtcDateTime, kept.ExpiresAt.UtcDateTime, TimeSpan.FromMilliseconds(1));

        Clock.Advance(PastTheLimit);
        using var third = await SignupApi.Request(Client, "ANNA@firma.TEST", "Second");
        await AccountApi.AssertEmptyAsync(third, HttpStatusCode.Accepted);

        var pending = Assert.Single(await PendingAsync());
        Assert.Equal("ANNA@firma.TEST", pending.Email);
        Assert.Equal("Second", pending.CompanyName);
    }

    [Fact]
    public async Task A_second_request_within_a_minute_is_refused_and_asking_again_does_not_add_to_the_wait()   // criterion 6, Review Focus 3
    {
        using var first = await SignupApi.Request(Client, Address, CompanyName);
        await AccountApi.AssertEmptyAsync(first, HttpStatusCode.Accepted);

        using var second = await SignupApi.Request(Client, Address, CompanyName);
        Assert.Equal(60, await LockoutApi.AssertLockedAsync(second));

        Clock.Advance(TimeSpan.FromSeconds(20));
        using var third = await SignupApi.Request(Client, Address, CompanyName);
        Assert.Equal(40, await LockoutApi.AssertLockedAsync(third));

        Clock.Advance(TimeSpan.FromSeconds(40));
        using var fourth = await SignupApi.Request(Client, Address, CompanyName);
        await AccountApi.AssertEmptyAsync(fourth, HttpStatusCode.Accepted);
        Assert.Equal(2, await QueueCountAsync());
    }

    [Fact]
    public async Task The_sixth_request_within_the_hour_is_refused_until_the_hour_is_over()   // criterion 6
    {
        for (var i = 0; i < 5; i++)
        {
            using var accepted = await SignupApi.Request(Client, Address, CompanyName);
            await AccountApi.AssertEmptyAsync(accepted, HttpStatusCode.Accepted);
            Clock.Advance(PastTheLimit);
        }

        using var sixth = await SignupApi.Request(Client, Address, CompanyName);
        Assert.Equal(3600 - 305, await LockoutApi.AssertLockedAsync(sixth));

        Clock.Advance(TimeSpan.FromSeconds(3600 - 305));
        using var later = await SignupApi.Request(Client, Address, CompanyName);
        await AccountApi.AssertEmptyAsync(later, HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task The_sign_up_limit_and_the_limits_of_forgot_and_verify_do_not_affect_each_other()   // criterion 6
    {
        using var signup = await SignupApi.Request(Client, Address, CompanyName);
        await AccountApi.AssertEmptyAsync(signup, HttpStatusCode.Accepted);

        using var forgot = await AccountApi.Forgot(Client, Address);
        using var verify = await AccountApi.RequestVerification(Client, Address);
        await AccountApi.AssertEmptyAsync(forgot, HttpStatusCode.Accepted);
        await AccountApi.AssertEmptyAsync(verify, HttpStatusCode.Accepted);

        using var otherForgot = await AccountApi.Forgot(Client, "other@firma.test");
        await AccountApi.AssertEmptyAsync(otherForgot, HttpStatusCode.Accepted);
        using var otherSignup = await SignupApi.Request(Client, "other@firma.test", CompanyName);
        await AccountApi.AssertEmptyAsync(otherSignup, HttpStatusCode.Accepted);

        Assert.Equal(5, await LimitCountAsync());   // three kinds for one address, two for the other
    }

    [Fact]
    public async Task A_second_accepted_request_replaces_the_first()   // criterion 7
    {
        using var first = await SignupApi.Request(Client, Address, "First");
        await AccountApi.AssertEmptyAsync(first, HttpStatusCode.Accepted);
        Clock.Advance(PastTheLimit);

        using var second = await SignupApi.Request(Client, Address, "Second");
        await AccountApi.AssertEmptyAsync(second, HttpStatusCode.Accepted);

        var pending = Assert.Single(await PendingAsync());
        Assert.Equal("Second", pending.CompanyName);
        Assert.Equal(Clock.GetUtcNow().AddHours(24).UtcDateTime, pending.ExpiresAt.UtcDateTime, TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, await QueueCountAsync());
    }

    [Fact]
    public async Task An_accepted_request_is_recorded_with_the_typed_address_only()   // criterion 19
    {
        var body = """{"email":"Anna@Firma.test","company_name":"Firma"}""";

        using var response = await RateLimitApi.SendAsync(Client, HttpMethod.Post, SignupApi.RequestPath, CallerAddress, null, body);
        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);

        var row = await SingleAsync(AuditKinds.SignupRequested);
        Assert.Equal("Anna@Firma.test", row.SubjectEmail);
        Assert.Equal(CallerAddress, row.ClientIp);
        Assert.Null(row.ActorUserId);
        Assert.Null(row.SubjectUserId);
        Assert.Null(row.OrgId);
        Assert.Null(row.OrgName);
        Assert.Null(row.TargetId);
        Assert.Null(row.Details);
    }

    [Fact]
    public async Task An_address_of_254_characters_is_accepted_and_stored_whole()
    {
        var address = new string('a', 243) + "@firma.test";
        Assert.Equal(254, address.Length);

        using var response = await SignupApi.Request(Client, address, CompanyName);

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        var pending = Assert.Single(await PendingAsync());
        Assert.Equal(address, pending.Email);
    }

    [Fact]
    public async Task A_sign_up_submitted_without_the_address_or_the_company_name_is_refused_and_leaves_nothing()
    {
        var store = Factory.Services.GetRequiredService<MailRequestStore>();

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => store.SubmitAsync(MailKind.Signup, "ANNA@FIRMA.TEST", TestContext.Current.CancellationToken, typedEmail: "anna@firma.test"));

        Assert.Equal("companyName", error.ParamName);
        Assert.Equal(0, await LimitCountAsync());
        Assert.Equal(0, await QueueCountAsync());
        Assert.Empty(await PendingAsync());
    }

    [Fact]
    public async Task A_refused_request_is_not_recorded()   // criterion 19: only an accepted request
    {
        using var accepted = await SignupApi.Request(Client, Address, CompanyName);
        using var limited = await SignupApi.Request(Client, Address, CompanyName);
        using var malformed = await AccountApi.PostRaw(Client, SignupApi.RequestPath, "{}");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Single(await AuditAsync(AuditKinds.SignupRequested));
    }
}
