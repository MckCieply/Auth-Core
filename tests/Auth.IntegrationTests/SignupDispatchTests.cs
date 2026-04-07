using System.Net;
using System.Text.RegularExpressions;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class SignupDispatchTests(PostgresFixture postgres, KeyMaterialFixture keys) : SignupTestBase(postgres, keys)
{
    // Two different addresses that the account lookup folds into one key.
    private static readonly string Micro = char.ConvertFromUtf32(0xB5);
    private static readonly string Mu = char.ConvertFromUtf32(0x3BC);

    private Task<string> DumpAsync() =>
        InDbAsync(async db => string.Join(' ', await db.Database.SqlQuery<string>(
            $"""
            SELECT coalesce(string_agg(row_to_json(p)::text, ' '), '') AS "Value" FROM "PendingSignups" p
            UNION ALL SELECT coalesce(string_agg(row_to_json(m)::text, ' '), '') FROM "MailRequests" m
            UNION ALL SELECT coalesce(string_agg(row_to_json(t)::text, ' '), '') FROM "EmailTokens" t
            UNION ALL SELECT coalesce(string_agg(row_to_json(a)::text, ' '), '') FROM audit_events a
            """).ToListAsync(TestContext.Current.CancellationToken)));

    [Fact]
    public async Task A_new_address_gets_the_complete_mail_with_the_link_and_the_company_name()   // criteria 3, 21
    {
        var token = await MailedAsync();

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal(Address, mail.To);
        Assert.StartsWith("Complete your sign-up", mail.Subject, StringComparison.Ordinal);
        Assert.Contains("Auth-Core Test", mail.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain(CompanyName, mail.Subject, StringComparison.Ordinal);
        Assert.Contains(AuthAppFactory.DefaultCompleteSignupUrl + "?token=" + token, mail.TextBody, StringComparison.Ordinal);
        Assert.Contains(CompanyName, mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("valid for 24 hours", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains($"href=\"{AuthAppFactory.DefaultCompleteSignupUrl}?token={token}\"", mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(CompanyName, mail.HtmlBody, StringComparison.Ordinal);
        Assert.Equal(0, await QueueCountAsync());
        // The same link in the text and in the HTML part.
        var links = Regex.Matches(mail.TextBody + mail.HtmlBody, @"complete-signup\?token=([A-Za-z0-9_-]{43})");
        Assert.All(links, link => Assert.Equal(token, link.Groups[1].Value));
    }

    [Fact]
    public async Task Only_the_hash_of_the_token_is_stored_and_the_24_hours_start_at_the_mail()   // criteria 3, 13
    {
        using var response = await SignupApi.Request(Client, Address, CompanyName);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Clock.Advance(TimeSpan.FromMinutes(30));   // the request was made earlier than its mail (within the hour the queue keeps it)

        await DispatchAsync();

        var token = TokenIn(Assert.Single(Mail.Sent));
        var row = Assert.Single(await PendingAsync());
        Assert.Equal(43, token.Length);
        Assert.Equal(EmailTokens.HashOf(token), row.TokenHash);
        Assert.Equal(StorableTime.Now(Clock) + TimeSpan.FromHours(24), row.TokenExpiresAt);
        Assert.Equal(StorableTime.Now(Clock) + TimeSpan.FromHours(24), row.ExpiresAt);
        Assert.DoesNotContain(token, await DumpAsync(), StringComparison.Ordinal);   // the token in clear is in no row of any table that could hold it
    }

    [Fact]
    public async Task Names_at_the_limit_with_polish_letters_and_pairs_reach_the_mail_whole()   // Review Focus 4
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
            using var response = await SignupApi.Request(Client, $"anna{i}@firma.test", names[i]);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        await DispatchAsync();

        for (var i = 0; i < names.Length; i++)
        {
            var mail = Mail.Sent.Single(m => m.To == $"anna{i}@firma.test");
            Assert.Contains(names[i], mail.TextBody, StringComparison.Ordinal);
            Assert.Contains(names[i], WebUtility.HtmlDecode(mail.HtmlBody), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_second_mail_replaces_the_link_of_the_first()   // criterion 7
    {
        var first = await MailedAsync(company: "First");
        Clock.Advance(PastTheLimit);

        var second = await MailedAsync(company: "Second");

        Assert.NotEqual(first, second);
        Assert.Equal(2, Mail.Sent.Count);
        Assert.Contains("Second", Mail.Sent[1].TextBody, StringComparison.Ordinal);
        var row = Assert.Single(await PendingAsync());
        Assert.Equal(EmailTokens.HashOf(second), row.TokenHash);
        Assert.Equal("Second", row.CompanyName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_address_with_an_account_gets_the_account_exists_mail_and_the_pending_sign_up_is_gone(bool confirmed)   // criterion 4: an account that belongs to a company
    {
        await CreateUserAsync(Address, confirmed);
        using var response = await SignupApi.Request(Client, Address, CompanyName);
        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);

        Assert.Equal(1, await DispatchAsync());

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal(Address, mail.To);
        Assert.StartsWith("You already have an account", mail.Subject, StringComparison.Ordinal);
        foreach (var part in new[] { mail.Subject, mail.TextBody, mail.HtmlBody })
        {
            Assert.DoesNotContain("://", part, StringComparison.Ordinal);
            Assert.DoesNotContain("token", part, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(await PendingAsync());
        Assert.Equal(0, await QueueCountAsync());
        Assert.Equal(0, await InDbAsync(db => db.EmailTokens.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task The_account_exists_mail_goes_to_the_stored_address_spelled_as_stored()   // Review Focus 2
    {
        await CreateUserAsync("Anna.Firma@Firma.Test", confirmed: true);
        using var response = await SignupApi.Request(Client, "anna.firma@firma.test", CompanyName);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await DispatchAsync();

        Assert.Equal("Anna.Firma@Firma.Test", Assert.Single(Mail.Sent).To);
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("expired")]
    public async Task A_sign_up_whose_pending_sign_up_is_gone_and_whose_address_has_no_account_sends_nothing(string how)   // the mail bullets of spec 0009
    {
        if (how == "expired")
        {
            // A row that has expired but not yet been pruned, and a request queued just now: the queue must not take it for a live one.
            await InDbAsync(async db =>
            {
                var now = StorableTime.Now(Clock);
                db.PendingSignups.Add(new PendingSignup
                {
                    NormalizedEmail = "ANNA@FIRMA.TEST",
                    Email = Address,
                    CompanyName = CompanyName,
                    RequestedAt = now - TimeSpan.FromHours(30),
                    ExpiresAt = now - TimeSpan.FromHours(6),
                });
                db.MailRequests.Add(new MailRequest { Kind = MailKind.Signup, NormalizedEmail = "ANNA@FIRMA.TEST", RequestedAt = now, NextAttemptAt = now });
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                return 0;
            });
        }
        else
        {
            using var response = await SignupApi.Request(Client, Address, CompanyName);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            await InDbAsync(db => db.PendingSignups.ExecuteDeleteAsync(TestContext.Current.CancellationToken));
        }

        Assert.Equal(1, await DispatchAsync());

        Assert.Empty(Mail.Sent);
        Assert.Equal(0, await QueueCountAsync());
    }

    [Fact]
    public async Task An_address_with_an_account_gets_the_notice_even_when_its_pending_sign_up_is_gone()   // the owner's ruling: the account check comes first
    {
        await CreateUserAsync("Anna.Firma@Firma.Test", confirmed: true);
        using var response = await SignupApi.Request(Client, "anna.firma@firma.test", CompanyName);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await InDbAsync(db => db.PendingSignups.ExecuteDeleteAsync(TestContext.Current.CancellationToken));   // completed, or replaced by an account, since

        Assert.Equal(1, await DispatchAsync());

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal("Anna.Firma@Firma.Test", mail.To);
        Assert.StartsWith("You already have an account", mail.Subject, StringComparison.Ordinal);
        Assert.Equal(0, await QueueCountAsync());
        Assert.Empty(await PendingAsync());
    }

    [Fact]
    public async Task An_account_made_while_the_dispatcher_waits_for_the_pending_sign_up_still_gets_the_notice()   // the second look at the accounts
    {
        using var response = await SignupApi.Request(Client, Address, CompanyName);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // A completion that is under way: it has deleted the pending sign-up and not yet committed.
        Task<int> dispatch;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            await using var completion = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await db.PendingSignups.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

            dispatch = DispatchAsync();   // locks the row it has been told to expect, and so waits for the completion

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (await InDbAsync(d => d.Database.SqlQuery<long>($"""SELECT count(*) AS "Value" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'""").SingleAsync(TestContext.Current.CancellationToken)) != 1)
            {
                Assert.True(DateTime.UtcNow < deadline, "The dispatcher never waited for the lock of the pending sign-up.");
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            Assert.False(dispatch.IsCompleted);
            await CreateUserAsync(Address, confirmed: true);   // the account the completion makes: it has a company
            await completion.CommitAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, await dispatch);

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal(Address, mail.To);
        Assert.StartsWith("You already have an account", mail.Subject, StringComparison.Ordinal);
        Assert.Equal(0, await QueueCountAsync());
        Assert.Empty(await PendingAsync());
    }

    [Fact]
    public async Task An_address_with_an_account_and_an_expired_pending_sign_up_gets_the_notice_and_loses_the_row()
    {
        await CreateUserAsync(Address, confirmed: true);
        await InDbAsync(async db =>
        {
            var now = StorableTime.Now(Clock);
            // A row that has expired but not yet been pruned, and the request that was queued while it was alive.
            db.PendingSignups.Add(new PendingSignup
            {
                NormalizedEmail = "ANNA@FIRMA.TEST",
                Email = Address,
                CompanyName = CompanyName,
                RequestedAt = now - TimeSpan.FromHours(30),
                ExpiresAt = now - TimeSpan.FromHours(6),
            });
            db.MailRequests.Add(new MailRequest { Kind = MailKind.Signup, NormalizedEmail = "ANNA@FIRMA.TEST", RequestedAt = now, NextAttemptAt = now });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return 0;
        });

        await DispatchAsync();

        Assert.StartsWith("You already have an account", Assert.Single(Mail.Sent).Subject, StringComparison.Ordinal);
        Assert.Empty(await PendingAsync());
    }

    [Fact]
    public async Task A_request_for_a_gone_pending_sign_up_is_dropped_by_the_bulk_delete()
    {
        await InDbAsync(async db =>
        {
            var now = StorableTime.Now(Clock);
            db.MailRequests.Add(new MailRequest { Kind = MailKind.Signup, NormalizedEmail = "NOBODY@FIRMA.TEST", RequestedAt = now, NextAttemptAt = now });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return 0;
        });

        Assert.Equal(1, await DispatchAsync());

        Assert.Empty(Mail.Sent);
        Assert.Equal(0, await QueueCountAsync());
    }

    [Fact]
    public async Task A_failed_send_leaves_the_previous_link_valid_and_the_retry_sends_a_new_one()   // Review Focus 5
    {
        var first = await MailedAsync(company: "First");
        Clock.Advance(PastTheLimit);
        using var again = await SignupApi.Request(Client, Address, "Second");
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);

        Mail.Failing = true;
        Assert.Equal(1, await DispatchAsync());

        Assert.Single(Mail.Sent);   // nothing new went out
        var afterFailure = Assert.Single(await PendingAsync());
        Assert.Equal(EmailTokens.HashOf(first), afterFailure.TokenHash);   // the earlier link is still the one that works
        Assert.Equal("Second", afterFailure.CompanyName);
        Assert.Equal(1, await QueueCountAsync());

        Mail.Failing = false;
        Clock.Advance(TimeSpan.FromSeconds(5));   // the first retry
        Assert.Equal(1, await DispatchAsync());

        Assert.Equal(2, Mail.Sent.Count);
        var second = TokenIn(Mail.Sent[1]);
        Assert.NotEqual(first, second);
        Assert.Equal(EmailTokens.HashOf(second), Assert.Single(await PendingAsync()).TokenHash);
        Assert.Equal(0, await QueueCountAsync());
    }

    [Fact]
    public async Task A_request_for_a_look_alike_spelling_drops_the_earlier_link_even_when_its_mail_fails()   // a link proves the mailbox it went to; the key folds the micro sign into mu
    {
        string micro = Micro + "@firma.test", mu = Mu + "@firma.test";
        using (var scope = Factory.Services.CreateScope())
        {
            var normalizer = scope.ServiceProvider.GetRequiredService<ILookupNormalizer>();
            Assert.NotEqual(micro, mu);
            Assert.Equal(normalizer.NormalizeEmail(micro), normalizer.NormalizeEmail(mu));   // the premise: one key, two mailboxes
        }

        var first = await MailedAsync(micro);   // link 1 went to the first mailbox
        Clock.Advance(PastTheLimit);
        using var again = await SignupApi.Request(Client, mu, CompanyName);
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);

        Mail.Failing = true;   // the mail to the second mailbox does not go out
        Assert.Equal(1, await DispatchAsync());

        var row = Assert.Single(await PendingAsync());
        Assert.Equal(mu, row.Email);
        Assert.Null(row.TokenHash);   // the earlier link no longer stands for this sign-up
        Assert.Null(row.TokenExpiresAt);
        Assert.Equal(StorableTime.Now(Clock) + TimeSpan.FromHours(24), row.ExpiresAt);   // the row lives from this request alone
        using (var stale = await SignupApi.Complete(Client, first, Password, CompanyName))
        {
            await AccountApi.AssertErrorAsync(stale, AccountApi.InvalidToken);
        }

        Assert.Equal(0, await InDbAsync(db => db.Users.CountAsync(u => u.NormalizedEmail == row.NormalizedEmail, TestContext.Current.CancellationToken)));
        Assert.Single(await PendingAsync());   // the refused completion left the sign-up in place

        Mail.Failing = false;   // the retry reaches the second mailbox, with a link of its own
        Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, await DispatchAsync());
        var second = TokenIn(Mail.Sent[^1]);
        Assert.Equal(mu, Mail.Sent[^1].To);
        Assert.NotEqual(first, second);
        using var done = await SignupApi.Complete(Client, second, Password, CompanyName);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
    }

    [Fact]
    public async Task A_request_that_differs_only_in_ascii_case_keeps_the_earlier_link_while_its_mail_fails()   // the same mailbox: the rule of the previous test must not touch it
    {
        var first = await MailedAsync("Anna@Firma.test");
        Clock.Advance(PastTheLimit);
        using var again = await SignupApi.Request(Client, "ANNA@firma.TEST", "Second");
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);

        Mail.Failing = true;
        Assert.Equal(1, await DispatchAsync());

        var row = Assert.Single(await PendingAsync());
        Assert.Equal("ANNA@firma.TEST", row.Email);   // the newest spelling and company name
        Assert.Equal("Second", row.CompanyName);
        Assert.Equal(EmailTokens.HashOf(first), row.TokenHash);   // link 1 still works
        Assert.NotNull(row.TokenExpiresAt);
        using var done = await SignupApi.Complete(Client, first, Password, "Second");
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
    }

    [Fact]
    public async Task A_failed_send_to_an_account_keeps_the_pending_sign_up_until_the_mail_is_sent()   // Review Focus 5
    {
        await CreateUserAsync(Address, confirmed: true);
        using var response = await SignupApi.Request(Client, Address, CompanyName);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        Mail.Failing = true;
        await DispatchAsync();
        Assert.Empty(Mail.Sent);
        Assert.Single(await PendingAsync());

        Mail.Failing = false;
        Clock.Advance(TimeSpan.FromSeconds(5));
        await DispatchAsync();

        Assert.StartsWith("You already have an account", Assert.Single(Mail.Sent).Subject, StringComparison.Ordinal);
        Assert.Empty(await PendingAsync());
    }

    [Fact]
    public async Task Pending_sign_ups_are_invisible_to_login_and_forgot_password()   // criterion 15
    {
        using var request = await SignupApi.Request(Client, Address, CompanyName);
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);

        using var pending = await LoginApi.Login(Client, Address, UserPassword);
        using var unknown = await LoginApi.Login(Client, "nobody@firma.test", UserPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, pending.StatusCode);
        Assert.Equal(unknown.StatusCode, pending.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await pending.Content.ReadAsStringAsync());
        Assert.Equal(LoginApi.HeaderNames(unknown), LoginApi.HeaderNames(pending));

        using var forgot = await AccountApi.Forgot(Client, Address);
        using var verify = await AccountApi.RequestVerification(Client, Address);
        Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, verify.StatusCode);
        await DispatchAsync();
        // Only the sign-up mail went out: the reset and the verification found no account.
        var mail = Assert.Single(Mail.Sent);
        Assert.StartsWith("Complete your sign-up", mail.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invitations_do_not_see_a_pending_sign_up()   // spec 0009 → Pending sign-ups
    {
        using var request = await SignupApi.Request(Client, Address, CompanyName);
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, Address, "user");
        await EnqueueInvitationAsync(invite, Address);
        await DispatchAsync();
        var inviteToken = TokenIn(Mail.Sent.Single(m => m.Subject.StartsWith("You are invited", StringComparison.Ordinal)));

        using (var preview = await TenancyApi.Preview(Client, inviteToken))
        {
            var body = await TenancyApi.ReadOkAsync(preview);   // as for an address nobody has: no already_member, no invalid_token
            Assert.Equal("Acme", body.GetProperty("org_name").GetString());
        }

        using (var accepted = await TenancyApi.Accept(Client, inviteToken, "Invited-Passw0rd"))
        {
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        }

        Assert.Single(await PendingAsync());   // the invitation neither used nor deleted it
    }
}

public sealed class SignupDispatchPolishTests : SignupTestBase
{
    public SignupDispatchPolishTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithSetting(MailSettingsLoader.LocaleKey, "pl");
    }

    [Fact]
    public async Task The_mails_are_in_the_configured_language()   // criterion 21
    {
        await MailedAsync(company: "Żółw i Spółka");
        await CreateUserAsync("ola@firma.test", confirmed: true);
        using var response = await SignupApi.Request(Client, "ola@firma.test", CompanyName);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await DispatchAsync();

        var complete = Mail.Sent.Single(m => m.To == Address);
        Assert.Equal("Dokończ rejestrację w Auth-Core Test", complete.Subject);
        Assert.Contains("Żółw i Spółka", complete.TextBody, StringComparison.Ordinal);
        Assert.Contains("ważny przez 24 godziny", complete.TextBody, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"pl\">", complete.HtmlBody, StringComparison.Ordinal);
        var exists = Mail.Sent.Single(m => m.To == "ola@firma.test");
        Assert.Equal("Masz już konto w Auth-Core Test", exists.Subject);
        Assert.Contains("już istnieje", exists.TextBody, StringComparison.Ordinal);
    }
}
