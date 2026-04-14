using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → Mails: the switch of the kind first, any account gets the notice, the private variant.</summary>
public sealed class PrivateSignupDispatchTests : SignupTestBase
{
    private const string Private = "ola@poczta.test";
    private const string Sentence = "If you want a new account on purpose, delete the one you have first, or sign up with another email address.";

    private static readonly string Mu = char.ConvertFromUtf32(0x3BC);
    private static readonly string Micro = char.ConvertFromUtf32(0xB5);
    private static readonly string FinalSigma = char.ConvertFromUtf32(0x3C2);
    private static readonly string Sigma = char.ConvertFromUtf32(0x3C3);

    /// <summary>
    /// Two different addresses that the account lookup folds into one (the pairs of <c>InviteAcceptTests</c>), the kind of the pending sign-up
    /// that names the second, and the kind of the account that has the first.
    /// </summary>
    public static TheoryData<string, string, string, string> LookAlikes
    {
        get
        {
            TheoryData<string, string, string, string> data = [];
            foreach (var (account, typed) in new[]
            {
                (Mu + "aria@poczta.test", Micro + "aria@poczta.test"),
                ("kosta" + FinalSigma + "@poczta.test", "kosta" + Sigma + "@poczta.test"),
            })
            {
                foreach (var pendingKind in new[] { "organization", "private" })
                {
                    foreach (var accountKind in new[] { "private", "company" })
                    {
                        data.Add(account, typed, pendingKind, accountKind);
                    }
                }
            }

            return data;
        }
    }

    public PrivateSignupDispatchTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithPrivateSignup();
    }

    private Task<HttpResponseMessage> RequestAsync(string kind, string email = Private) =>
        kind == "private" ? SignupApi.RequestPrivate(Client, email) : SignupApi.Request(Client, email, CompanyName);

    /// <summary>Turns the two switches as a restart with another manifest would.</summary>
    private void Turn(bool organization, bool @private)
    {
        var current = Holder.Current;
        Holder.Set(
            new Manifest(current.Permissions, current.DefaultRoles, organization ? SignupMode.SelfService : SignupMode.InviteOnly, @private, current.PrivatePermissions),
            null);
    }

    private static void AssertNotice(ComposedMailView mail)
    {
        Assert.StartsWith("You already have an account", mail.Subject, StringComparison.Ordinal);
        Assert.Contains(Sentence, mail.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", mail.Text + mail.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("://", mail.Text + mail.Html, StringComparison.Ordinal);
    }

    private sealed record ComposedMailView(string Subject, string Text, string Html);

    private ComposedMailView OnlyMail()
    {
        var mail = Assert.Single(Mail.Sent);
        return new ComposedMailView(mail.Subject, mail.TextBody, mail.HtmlBody);
    }

    [Fact]
    public async Task A_private_request_gets_the_private_mail_with_a_link()   // criterion 4
    {
        using (var response = await RequestAsync("private"))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        await DispatchAsync();

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal(Private, mail.To);
        Assert.Equal("Complete your sign-up for " + AuthAppFactory.DefaultAppName, mail.Subject);
        Assert.Contains(
            "Someone asked to create a private account on " + AuthAppFactory.DefaultAppName + " with this email address.",
            mail.TextBody,
            StringComparison.Ordinal);
        Assert.Contains(AuthAppFactory.DefaultCompleteSignupUrl + "?token=", mail.TextBody, StringComparison.Ordinal);
        Assert.NotNull(Assert.Single(await PendingAsync()).TokenHash);
    }

    [Theory]
    [InlineData("organization")]
    [InlineData("private")]
    public async Task An_address_with_a_private_account_gets_the_account_exists_mail_with_the_new_sentence(string kind)   // criterion 5
    {
        await CreatePrivateAccountAsync(Private);

        using (var response = await RequestAsync(kind))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        await DispatchAsync();

        AssertNotice(OnlyMail());
        Assert.Empty(await PendingAsync());
    }

    [Fact]
    public async Task An_address_with_a_company_account_gets_the_notice_for_a_private_request_too()   // criterion 5
    {
        using (var response = await RequestAsync("private", Factory.SeedEmail))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        await DispatchAsync();

        Assert.Equal(Factory.SeedEmail, Assert.Single(Mail.Sent).To);
        AssertNotice(OnlyMail());
        Assert.Empty(await PendingAsync());
    }

    [Theory]
    [InlineData("organization", false, true)]
    [InlineData("private", true, false)]
    [InlineData("private", false, false)]
    public async Task A_mail_whose_kinds_switch_was_turned_off_is_dropped_and_nothing_is_sent(string kind, bool organization, bool @private)   // Mails item 1; criterion 3
    {
        using (var response = await RequestAsync(kind))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        Turn(organization, @private);
        await DispatchAsync();

        Assert.Empty(Mail.Sent);
        Assert.Empty(Mail.Attempted);
        Assert.Equal(0, await QueueCountAsync());
    }

    [Fact]
    public async Task A_private_pending_sign_up_still_gets_its_link_while_only_private_sign_up_is_on()   // Mails item 1: the bulk delete takes only what its own kind's switch condemns
    {
        using (var response = await RequestAsync("private"))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        Turn(organization: false, @private: true);
        await DispatchAsync();

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal(Private, mail.To);
        Assert.Contains(AuthAppFactory.DefaultCompleteSignupUrl + "?token=", mail.TextBody, StringComparison.Ordinal);
        Assert.NotNull(Assert.Single(await PendingAsync()).TokenHash);
    }

    [Theory]
    [InlineData("private", true, false)]
    [InlineData("organization", false, true)]
    public async Task A_kind_turned_off_drops_its_mail_even_when_the_address_has_an_account(string kind, bool organization, bool @private)   // Mails item 1 comes before item 2
    {
        await CreatePrivateAccountAsync(Private);
        using (var response = await RequestAsync(kind))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        Turn(organization, @private);
        await DispatchAsync();

        Assert.Empty(Mail.Sent);
        Assert.Empty(Mail.Attempted);
        Assert.Equal(0, await QueueCountAsync());
    }

    [Fact]
    public async Task A_private_mail_that_comes_up_after_private_sign_up_was_turned_off_during_the_pass_is_dropped()   // the single-row path looks at the switch of the kind
    {
        using (var first = await RequestAsync("private", "first@poczta.test"))
        using (var second = await RequestAsync("private", "second@poczta.test"))
        {
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        }

        // Both are live when the pass starts; private sign-up is turned off while the first mail is being sent.
        Mail.DuringSend = () =>
        {
            Turn(organization: true, @private: false);
            return Task.CompletedTask;
        };
        var mark = Logs.Entries.Count;

        Assert.Equal(2, await DispatchAsync());

        Assert.Equal("first@poczta.test", Assert.Single(Mail.Sent).To);   // sent before it was turned off
        Assert.Single(Mail.Attempted);                                    // nothing was tried for the second
        Assert.Equal(0, await QueueCountAsync());
        Assert.DoesNotContain(Logs.Entries.Skip(mark), e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task A_kind_turned_off_during_the_pass_drops_the_mail_of_an_address_that_has_an_account_without_the_notice()   // Mails item 1 before item 2, in the single-row path
    {
        await CreatePrivateAccountAsync("jan@poczta.test");
        using (var first = await RequestAsync("private", "first@poczta.test"))
        using (var second = await RequestAsync("private", "jan@poczta.test"))
        {
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        }

        // Both are live when the pass starts, so the bulk delete leaves both; private sign-up is turned off while the first mail is being sent.
        Mail.DuringSend = () =>
        {
            Turn(organization: true, @private: false);
            return Task.CompletedTask;
        };

        Assert.Equal(2, await DispatchAsync());

        Assert.Equal("first@poczta.test", Assert.Single(Mail.Sent).To);   // no notice to the account: its mail is dropped, not answered
        Assert.Single(Mail.Attempted);
        Assert.Equal(0, await QueueCountAsync());
        Assert.Contains(await PendingAsync(), p => p.Email == "jan@poczta.test");   // the notice would have deleted it
    }

    [Theory]
    [MemberData(nameof(LookAlikes))]
    public async Task An_account_that_only_looks_like_the_typed_address_gets_the_notice_at_its_own_address(
        string account, string typed, string pendingKind, string accountKind)   // Mails item 2: the link proves the typed mailbox only
    {
        string normalized;
        using (var scope = Factory.Services.CreateScope())
        {
            var normalizer = scope.ServiceProvider.GetRequiredService<ILookupNormalizer>();
            normalized = normalizer.NormalizeEmail(account);
            Assert.Equal(normalized, normalizer.NormalizeEmail(typed));   // the premise: the lookup folds the two together
        }

        if (accountKind == "private")
        {
            await CreatePrivateAccountAsync(account);
        }
        else
        {
            await AddMemberAsync(await CreateCompanyAsync("Acme"), account, "user");
        }

        // The pending sign-up and its queued request, made straight in the database: what the request would have left (the address rule of the
        // request is not what this test is about).
        var kind = pendingKind == "private" ? SignupKind.Private : SignupKind.Organization;
        await InDbAsync(async db =>
        {
            var now = StorableTime.Now(Clock);
            db.PendingSignups.Add(new PendingSignup
            {
                NormalizedEmail = normalized,
                Email = typed,
                CompanyName = kind == SignupKind.Private ? null : CompanyName,
                Kind = kind,
                RequestedAt = now,
                ExpiresAt = now + TimeSpan.FromHours(24),
            });
            db.MailRequests.Add(new MailRequest { Kind = MailKind.Signup, NormalizedEmail = normalized, RequestedAt = now, NextAttemptAt = now });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return 0;
        });

        Assert.Equal(1, await DispatchAsync());

        Assert.Equal(account, Assert.Single(Mail.Sent).To);   // the notice goes to the account's own address
        AssertNotice(OnlyMail());
        Assert.Empty(await PendingAsync());
    }

    [Fact]
    public async Task The_notice_goes_out_while_either_switch_is_on_once_the_pending_sign_up_is_gone()   // Mails items 1 and 2
    {
        await CreatePrivateAccountAsync(Private);
        using (var response = await RequestAsync("private"))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        await InDbAsync(db => db.PendingSignups.ExecuteDeleteAsync(TestContext.Current.CancellationToken));
        Turn(organization: true, @private: false);   // the kind's switch is off, but no pending sign-up is left to judge it by
        await DispatchAsync();

        AssertNotice(OnlyMail());
    }

    [Fact]
    public async Task With_both_switches_off_nothing_is_sent_even_to_an_account()   // Mails item 1
    {
        await CreatePrivateAccountAsync(Private);
        using (var response = await RequestAsync("private"))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        Turn(organization: false, @private: false);
        await DispatchAsync();

        Assert.Empty(Mail.Sent);
        Assert.Equal(0, await QueueCountAsync());
    }

    [Fact]
    public async Task A_pending_sign_up_of_an_address_that_has_an_account_is_deleted_by_the_notice()   // criterion 5
    {
        var link = await MailedAsync(Private);   // an organization link, before any account
        await CreatePrivateAccountAsync(Private);
        Clock.Advance(PastTheLimit);
        using (var again = await RequestAsync("organization"))
        {
            Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        }

        await DispatchAsync();

        AssertNotice(new ComposedMailView(Mail.Sent[^1].Subject, Mail.Sent[^1].TextBody, Mail.Sent[^1].HtmlBody));
        Assert.Empty(await PendingAsync());
        using var preview = await SignupApi.Preview(Client, link);
        await AccountApi.AssertErrorAsync(preview, AccountApi.InvalidToken);
    }
}
