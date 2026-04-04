using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0009, Amendment 3, mails: an address whose account belongs to no company gets the usual "complete your sign-up" mail when its pending
/// sign-up is live; an account that belongs to a company still gets the notice; the request itself is the same for every address.
/// </summary>
public sealed class SignupComebackDispatchTests(PostgresFixture postgres, KeyMaterialFixture keys) : SignupTestBase(postgres, keys)
{
    private static readonly string Mu = char.ConvertFromUtf32(0x3BC);
    private static readonly string Micro = char.ConvertFromUtf32(0xB5);
    private static readonly string FinalSigma = char.ConvertFromUtf32(0x3C2);
    private static readonly string Sigma = char.ConvertFromUtf32(0x3C3);

    /// <summary>Two different addresses that the account lookup folds into one (the pairs of <c>InviteAcceptTests</c>): the account's, and the typed one.</summary>
    public static TheoryData<string, string> FoldedPairs => new()
    {
        { Mu + "aria@firma.test", Micro + "aria@firma.test" },
        { "kosta" + FinalSigma + "@firma.test", "kosta" + Sigma + "@firma.test" },
    };

    /// <summary>The SQL of every database command the host has logged since <paramref name="mark"/> log entries.</summary>
    private List<string> StatementsSince(int mark) =>
        [.. Logs.Entries.Skip(mark)
            .Where(e => e.Category == "Microsoft.EntityFrameworkCore.Database.Command")
            .Select(e => e.Message[(e.Message.IndexOf('\n', StringComparison.Ordinal) + 1)..].Trim())];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_account_without_a_company_gets_the_complete_mail_with_the_link(bool confirmed)   // criterion 24
    {
        await RemovedMemberAsync(Address, confirmed);
        using var response = await SignupApi.Request(Client, Address, CompanyName);
        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);

        Assert.Equal(1, await DispatchAsync());   // the bulk delete did not take the row

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal(Address, mail.To);
        Assert.StartsWith("Complete your sign-up", mail.Subject, StringComparison.Ordinal);
        var token = TokenIn(mail);
        Assert.Contains(AuthAppFactory.DefaultCompleteSignupUrl + "?token=" + token, mail.TextBody, StringComparison.Ordinal);
        Assert.Contains($"href=\"{AuthAppFactory.DefaultCompleteSignupUrl}?token={token}\"", mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(CompanyName, mail.TextBody, StringComparison.Ordinal);
        var row = Assert.Single(await PendingAsync());
        Assert.Equal(EmailTokens.HashOf(token), row.TokenHash);
        Assert.Equal(StorableTime.Now(Clock) + TimeSpan.FromHours(24), row.TokenExpiresAt);
        Assert.Equal(0, await QueueCountAsync());
    }

    [Fact]
    public async Task The_complete_mail_goes_to_the_address_as_typed()   // Review Focus 2 of the plan: the link goes to the mailbox that asked
    {
        await RemovedMemberAsync("Anna.Back@Firma.Test");
        using var response = await SignupApi.Request(Client, "anna.back@firma.test", CompanyName);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await DispatchAsync();

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal("anna.back@firma.test", mail.To);
        Assert.StartsWith("Complete your sign-up", mail.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_account_that_belongs_to_a_company_gets_the_notice_and_no_link()   // criterion 4 and 24
    {
        var userId = await RemovedMemberAsync();
        await JoinACompanyAsync(userId);   // it lost one company and has another
        using var response = await SignupApi.Request(Client, Address, CompanyName);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        Assert.Equal(1, await DispatchAsync());

        var mail = Assert.Single(Mail.Sent);
        Assert.StartsWith("You already have an account", mail.Subject, StringComparison.Ordinal);
        foreach (var part in new[] { mail.Subject, mail.TextBody, mail.HtmlBody })
        {
            Assert.DoesNotContain("://", part, StringComparison.Ordinal);
            Assert.DoesNotContain("token", part, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(await PendingAsync());
        Assert.Equal(0, await QueueCountAsync());
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("expired")]
    public async Task A_sign_up_for_an_account_without_a_company_whose_pending_sign_up_is_gone_sends_nothing(string how)   // the mail bullets of spec 0009, Amendment 3
    {
        await RemovedMemberAsync();
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

        var mark = Logs.Entries.Count;
        Assert.Equal(1, await DispatchAsync());   // dropped by the bulk delete: no company, nothing to complete

        Assert.Empty(Mail.Sent);
        Assert.Equal(0, await QueueCountAsync());
        // The proof that it was the bulk delete and not the single-row path: that path locks the pending sign-up first (LockUsableAsync).
        Assert.DoesNotContain(StatementsSince(mark), sql => sql.StartsWith("SELECT * FROM \"PendingSignups\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_send_to_an_account_without_a_company_leaves_the_previous_link_valid()   // a failed sending leaves the previous link valid
    {
        await RemovedMemberAsync();
        var first = await MailedAsync(company: "First");
        Clock.Advance(PastTheLimit);
        using var again = await SignupApi.Request(Client, Address, "Second");
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);

        Mail.Failing = true;
        Assert.Equal(1, await DispatchAsync());

        Assert.Single(Mail.Sent);   // nothing new went out
        Assert.Equal(EmailTokens.HashOf(first), Assert.Single(await PendingAsync()).TokenHash);
        Assert.Equal(1, await QueueCountAsync());

        Mail.Failing = false;
        Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, await DispatchAsync());

        var second = TokenIn(Mail.Sent[1]);
        Assert.NotEqual(first, second);
        Assert.Equal(EmailTokens.HashOf(second), Assert.Single(await PendingAsync()).TokenHash);
    }

    [Fact]
    public async Task Sign_up_requests_that_arrive_during_a_pass_for_accounts_without_a_company_are_not_handled_one_by_one()   // the bulk delete takes them, not the single-row path
    {
        // One live sign-up request: the only row the first pass may handle singly.
        using var live = await SignupApi.Request(Client, "live@firma.test", CompanyName);
        Assert.Equal(HttpStatusCode.Accepted, live.StatusCode);

        // Thirty accounts without a company, none with a pending sign-up.
        for (var i = 0; i < 30; i++)
        {
            await RemovedMemberAsync($"gone-{i}@firma.test");
        }

        // While the first mail is being sent, a flood of sign-up requests for those addresses is queued.
        var flooded = false;
        Mail.DuringSend = async () =>
        {
            if (flooded)
            {
                return;
            }

            flooded = true;
            await InDbAsync(async db =>
            {
                var now = StorableTime.Now(Clock);
                for (var i = 0; i < 30; i++)
                {
                    db.MailRequests.Add(new MailRequest
                    {
                        Kind = MailKind.Signup,
                        NormalizedEmail = $"GONE-{i}@FIRMA.TEST",
                        RequestedAt = now,
                        NextAttemptAt = now,
                    });
                }

                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                return 0;
            });
        };

        Assert.Equal(1, await DispatchAsync());   // only the live one is handled in this pass

        Assert.Equal("live@firma.test", Assert.Single(Mail.Sent).To);
        var left = await InDbAsync(db => db.MailRequests.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(30, left.Count);
        Assert.All(left, row => Assert.Equal(0, row.Attempts));

        Assert.Equal(30, await DispatchAsync());   // the bulk delete of the next pass takes them all
        Assert.Equal(0, await QueueCountAsync());
        Assert.Single(Mail.Sent);   // and no mail goes out for them
    }

    [Theory]
    [MemberData(nameof(FoldedPairs))]
    public async Task An_account_that_only_looks_like_the_typed_address_gets_the_notice_and_no_link(string account, string typed)   // the link proves the typed mailbox only
    {
        string normalized;
        using (var scope = Factory.Services.CreateScope())
        {
            var normalizer = scope.ServiceProvider.GetRequiredService<ILookupNormalizer>();
            normalized = normalizer.NormalizeEmail(account);
            Assert.Equal(normalized, normalizer.NormalizeEmail(typed));   // the premise: the lookup folds the two together
        }

        await RemovedMemberAsync(account);
        // The pending sign-up and its queued request, made straight in the database: what the request would have left (the address rule of the
        // request is not what this test is about).
        await InDbAsync(async db =>
        {
            var now = StorableTime.Now(Clock);
            db.PendingSignups.Add(new PendingSignup
            {
                NormalizedEmail = normalized,
                Email = typed,
                CompanyName = CompanyName,
                RequestedAt = now,
                ExpiresAt = now + TimeSpan.FromHours(24),
            });
            db.MailRequests.Add(new MailRequest { Kind = MailKind.Signup, NormalizedEmail = normalized, RequestedAt = now, NextAttemptAt = now });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return 0;
        });

        Assert.Equal(1, await DispatchAsync());

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal(account, mail.To);   // the notice goes to the account's own address
        Assert.StartsWith("You already have an account", mail.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("token", mail.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await PendingAsync());
    }

    [Fact]
    public async Task An_account_that_joins_a_company_while_the_dispatcher_waits_for_the_pending_sign_up_gets_the_notice()   // the second look at the accounts
    {
        var userId = await RemovedMemberAsync();
        using var response = await SignupApi.Request(Client, Address, CompanyName);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // A completion that is under way: it has used the pending sign-up up and not yet committed.
        Task<int> dispatch;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            await using var completion = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await db.PendingSignups.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

            dispatch = DispatchAsync();   // sees an account without a company, then waits for the row that the completion has locked

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (await InDbAsync(d => d.Database.SqlQuery<long>($"""SELECT count(*) AS "Value" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'""").SingleAsync(TestContext.Current.CancellationToken)) != 1)
            {
                Assert.True(DateTime.UtcNow < deadline, "The dispatcher never waited for the lock of the pending sign-up.");
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            Assert.False(dispatch.IsCompleted);
            await JoinACompanyAsync(userId);   // what the completion gives the account
            await completion.CommitAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, await dispatch);

        var mail = Assert.Single(Mail.Sent);
        Assert.StartsWith("You already have an account", mail.Subject, StringComparison.Ordinal);
        Assert.Equal(0, await QueueCountAsync());
        Assert.Empty(await PendingAsync());
    }

    [Fact]
    public async Task An_account_that_joins_a_company_while_the_dispatcher_waits_and_the_pending_sign_up_survives_gets_the_notice()   // the account is read again after the lock
    {
        var userId = await RemovedMemberAsync();
        using var response = await SignupApi.Request(Client, Address, CompanyName);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Something else holds the row of the pending sign-up and changes nothing in it (a second dispatcher on another request of the
        // address, say); the invitation of the account is accepted while the dispatcher waits for that lock.
        Task<int> dispatch;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            await using var holder = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await db.Database.ExecuteSqlAsync(
                $"""SELECT 1 FROM "PendingSignups" WHERE "NormalizedEmail" = 'ANNA@FIRMA.TEST' FOR UPDATE""", TestContext.Current.CancellationToken);

            dispatch = DispatchAsync();   // sees an account without a company, then waits for the row

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (await InDbAsync(d => d.Database.SqlQuery<long>($"""SELECT count(*) AS "Value" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'""").SingleAsync(TestContext.Current.CancellationToken)) != 1)
            {
                Assert.True(DateTime.UtcNow < deadline, "The dispatcher never waited for the lock of the pending sign-up.");
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            Assert.False(dispatch.IsCompleted);
            await JoinACompanyAsync(userId);
            await holder.RollbackAsync(TestContext.Current.CancellationToken);   // nothing was changed in the row: it survives
        }

        Assert.Equal(1, await dispatch);

        var mail = Assert.Single(Mail.Sent);
        Assert.StartsWith("You already have an account", mail.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("token", mail.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await QueueCountAsync());
        Assert.Empty(await PendingAsync());   // the notice deletes it
    }
}
