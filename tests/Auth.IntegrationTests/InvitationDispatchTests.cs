using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

public sealed class InvitationDispatchTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private const string Address = "worker@acme.test";

    private Task<List<MailRequest>> QueueAsync() =>
        InDbAsync(db => db.MailRequests.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));

    private async Task<(Guid Company, Guid Invite)> InvitedAsync(string companyName = "Acme", string role = "user", string address = Address)
    {
        var company = await CreateCompanyAsync(companyName);
        var invite = await AddInviteAsync(company, address, role);
        await EnqueueInvitationAsync(invite, address);
        return (company, invite);
    }

    [Fact]
    public async Task Invitation_for_an_address_without_an_account_is_mailed_and_not_dropped()   // criterion 2
    {
        var (_, invite) = await InvitedAsync();

        Assert.Equal(1, await DispatchAsync());

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal(Address, mail.To);
        Assert.Contains(AuthAppFactory.DefaultInviteUrl + "?token=", mail.TextBody);
        Assert.Equal("Auth-Core Test", AuthAppFactory.DefaultAppName);
        Assert.Contains("Auth-Core Test", mail.Subject);
        Assert.Contains("Acme", mail.TextBody);
        Assert.Contains("as user", mail.TextBody);
        Assert.Contains("valid for 7 days", mail.TextBody);
        Assert.Empty(await QueueAsync());
        Assert.NotNull((await InviteAsync(invite))!.TokenHash);
    }

    [Fact]
    public async Task Only_the_hash_of_the_token_is_stored_and_the_seven_days_start_at_the_mail()   // criteria 2, 23
    {
        var company = await CreateCompanyAsync();
        var invite = await AddInviteAsync(company, Address, "user");
        Clock.Advance(TimeSpan.FromHours(3));   // the invitation was made earlier than its mail
        await EnqueueInvitationAsync(invite, Address);

        await DispatchAsync();

        var row = (await InviteAsync(invite))!;
        var token = TokenIn(Assert.Single(Mail.Sent));
        Assert.Equal(43, token.Length);
        Assert.Equal(EmailTokens.HashOf(token), row.TokenHash);
        Assert.Equal(StorableTime.Now(Clock) + InviteTokens.Lifetime, row.ExpiresAt);
        Assert.Equal(TimeSpan.FromDays(7), InviteTokens.Lifetime);

        // The token in clear is in no row of any table that could hold it.
        var dump = await InDbAsync(db => db.Database.SqlQuery<string>(
            $"""
            SELECT coalesce(string_agg(row_to_json(i)::text, ' '), '') AS "Value" FROM "Invites" i
            UNION ALL SELECT coalesce(string_agg(row_to_json(m)::text, ' '), '') FROM "MailRequests" m
            UNION ALL SELECT coalesce(string_agg(row_to_json(t)::text, ' '), '') FROM "EmailTokens" t
            """).ToListAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(token, string.Join(' ', dump));
    }

    [Fact]
    public async Task Invitation_to_an_address_that_has_an_account_is_mailed_too()
    {
        await CreateUserAsync(Address, confirmed: true, member: false);
        await InvitedAsync();

        await DispatchAsync();

        Assert.Equal(Address, Assert.Single(Mail.Sent).To);
    }

    [Fact]
    public async Task Recipient_is_the_address_as_the_inviter_typed_it()
    {
        await InvitedAsync(address: "Anna.Nowak@Acme.Test");

        await DispatchAsync();

        Assert.Equal("Anna.Nowak@Acme.Test", Assert.Single(Mail.Sent).To);
    }

    [Fact]
    public async Task A_new_mail_replaces_the_token_of_the_earlier_one_and_restarts_the_seven_days()   // criterion 10
    {
        var (_, invite) = await InvitedAsync();
        await DispatchAsync();
        var first = TokenIn(Assert.Single(Mail.Sent));

        Clock.Advance(TimeSpan.FromDays(1));
        await EnqueueInvitationAsync(invite, Address);
        await DispatchAsync();

        var second = TokenIn(Mail.Sent[1]);
        var row = (await InviteAsync(invite))!;
        Assert.NotEqual(first, second);
        Assert.Equal(EmailTokens.HashOf(second), row.TokenHash);
        Assert.NotEqual(EmailTokens.HashOf(first), row.TokenHash);
        Assert.Equal(StorableTime.Now(Clock) + InviteTokens.Lifetime, row.ExpiresAt);
    }

    [Fact]
    public async Task Failed_send_is_retried_on_the_schedule_and_the_earlier_link_stays_in_force()   // spec 0004, Decision 13
    {
        var (_, invite) = await InvitedAsync();
        await DispatchAsync();
        var first = TokenIn(Assert.Single(Mail.Sent));
        var expiry = (await InviteAsync(invite))!.ExpiresAt;

        Clock.Advance(TimeSpan.FromHours(1));
        await EnqueueInvitationAsync(invite, Address);
        Mail.Failing = true;
        await DispatchAsync();

        var kept = (await InviteAsync(invite))!;
        Assert.Equal(EmailTokens.HashOf(first), kept.TokenHash);   // the failed attempt left no new token behind
        Assert.Equal(expiry, kept.ExpiresAt);
        var queued = Assert.Single(await QueueAsync());
        Assert.Equal(1, queued.Attempts);

        Mail.Failing = false;
        Clock.Advance(MailDelivery.RetryDelay(1));
        await DispatchAsync();

        Assert.Equal(2, Mail.Sent.Count);
        Assert.Equal(EmailTokens.HashOf(TokenIn(Mail.Sent[1])), (await InviteAsync(invite))!.TokenHash);
        Assert.Empty(await QueueAsync());
    }

    [Fact]
    public async Task Request_for_an_invitation_that_was_cancelled_is_dropped_without_a_mail()
    {
        var (_, invite) = await InvitedAsync();
        await InDbAsync(db => db.Invites.Where(i => i.Id == invite).ExecuteDeleteAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1, await DispatchAsync());

        Assert.Empty(Mail.Sent);
        Assert.Empty(await QueueAsync());
    }

    [Fact]
    public async Task Request_without_an_invitation_is_dropped_without_a_mail()
    {
        await InDbAsync(async db =>
        {
            var now = StorableTime.Now(Clock);
            db.MailRequests.Add(new MailRequest { Kind = MailKind.Invitation, NormalizedEmail = "X@Y.Z", RequestedAt = now, NextAttemptAt = now });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return 0;
        });

        await DispatchAsync();

        Assert.Empty(Mail.Sent);
        Assert.Empty(await QueueAsync());
    }

    [Fact]
    public async Task Names_with_markup_are_encoded_in_the_mail_and_stay_out_of_the_headers()   // criterion 23
    {
        var company = await CreateCompanyAsync("Tom & <Jerry>");
        await InDbAsync(db => db.CompanyRoles.Where(r => r.CompanyId == company && r.Name == "user")
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.Name, "<b>boss</b>").SetProperty(r => r.NormalizedName, "<B>BOSS</B>"), TestContext.Current.CancellationToken));
        var invite = await InDbAsync(async db =>
        {
            var role = await db.CompanyRoles.AsNoTracking().SingleAsync(r => r.CompanyId == company && r.Name == "<b>boss</b>", TestContext.Current.CancellationToken);
            var now = StorableTime.Now(Clock);
            var row = new Invite
            {
                Id = Guid.NewGuid(),
                CompanyId = company,
                Email = Address,
                NormalizedEmail = Address.ToUpperInvariant(),
                RoleId = role.Id,
                InvitedAt = now,
                ExpiresAt = now + InviteTokens.Lifetime,
            };
            db.Invites.Add(row);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return row.Id;
        });
        await EnqueueInvitationAsync(invite, Address);

        await DispatchAsync();

        var mail = Assert.Single(Mail.Sent);
        Assert.Contains("Tom &amp; &lt;Jerry&gt;", mail.HtmlBody);
        Assert.Contains("&lt;b&gt;boss&lt;/b&gt;", mail.HtmlBody);
        Assert.DoesNotContain("Jerry", mail.Subject + mail.To);
        Assert.DoesNotContain("boss", mail.Subject + mail.To);
    }

    [Fact]
    public async Task No_token_and_no_address_reach_the_log()   // criterion 23
    {
        await InvitedAsync();
        Mail.Failing = true;
        await DispatchAsync();
        Mail.Failing = false;
        Clock.Advance(MailDelivery.RetryDelay(1));
        await DispatchAsync();

        var token = TokenIn(Assert.Single(Mail.Sent));
        Assert.DoesNotContain(token, Logs.Text);
        Assert.DoesNotContain(Address, Logs.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Logs.Entries, e => e.Message.Contains("Invitation"));   // the failure was logged, with the kind
    }

    [Fact]
    public async Task Requests_for_accounts_are_handled_as_before_beside_an_invitation()
    {
        await InvitedAsync();
        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);
        await EnqueueAsync(MailKind.PasswordReset, "nobody@example.com");

        Assert.Equal(3, await DispatchAsync());

        Assert.Equal(2, Mail.Sent.Count);
        Assert.Contains(Mail.Sent, m => m.To == Address);
        Assert.Contains(Mail.Sent, m => m.To == Factory.SeedEmail);
    }
}
