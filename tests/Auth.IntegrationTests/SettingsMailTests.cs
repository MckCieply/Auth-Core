using System.Net;
using Auth.FakeOidc;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0012 → Mails (criterion 22): the notices of a change to how an account signs in, written in the change's transaction, limited on their
/// own, sent by the dispatcher (run by hand here) to an account of the address whatever its confirmation, with no link and no token.
/// </summary>
public sealed class SettingsMailTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Ola = FakePeople.GoogleEmail;
    private const string OlaNormalized = "OLA@GMAIL.TEST";
    private const string LimitWarning = "notice limit";

    private static readonly MailKind[] NoticeKinds = [MailKind.ProviderLinked, MailKind.ProviderUnlinked, MailKind.PasswordChanged];

    private Task<List<MailRequest>> NoticesAsync() =>
        InDbAsync(db => db.MailRequests.AsNoTracking().Where(r => NoticeKinds.Contains(r.Kind)).OrderBy(r => r.Id)
            .ToListAsync(TestContext.Current.CancellationToken));

    private Task<MailRequestLimit?> LimitAsync(MailKind key) =>
        InDbAsync(db => db.MailRequestLimits.AsNoTracking()
            .SingleOrDefaultAsync(l => l.IdentifierHash == LoginIdentifier.HashOf(OlaNormalized) && l.Kind == key, TestContext.Current.CancellationToken));

    private int LimitWarnings() =>
        Logs.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains(LimitWarning, StringComparison.Ordinal));

    /// <summary>A notice queued as the endpoints queue it: in a transaction of the caller's, saved and committed, then the dispatcher woken.</summary>
    private async Task<bool> QueueAsync(MailKind kind, string normalizedEmail = OlaNormalized, string? provider = "google")
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var notices = Factory.Services.GetRequiredService<MailNotices>();
        await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var queued = await notices.QueueAsync(db, kind, normalizedEmail, provider, StorableTime.Now(Clock), TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return queued;
    }

    private static void AssertNoLink(ComposedMail mail)
    {
        foreach (var body in new[] { mail.TextBody, mail.HtmlBody })
        {
            Assert.DoesNotContain("http", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<a ", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_link_queues_provider_connected_in_its_transaction_and_the_mail_names_the_provider_with_no_link()   // criterion 22
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());

        using (var linked = await ProviderApi.Link(Client, ticket, UserPassword))
        {
            Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        }

        var row = Assert.Single(await NoticesAsync());
        Assert.Equal((MailKind.ProviderLinked, OlaNormalized, "google"), (row.Kind, row.NormalizedEmail, row.Provider));
        Assert.Equal(row.RequestedAt, (await SingleAsync(AuditKinds.ProviderLinked)).OccurredAt);   // one transaction, one clock reading
        Assert.Equal(0, await InDbAsync(db => db.EmailTokens.CountAsync(t => t.UserId == user.Id, TestContext.Current.CancellationToken)));

        await DispatchAsync();

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal(Ola, mail.To);
        Assert.Contains("Google", mail.Subject, StringComparison.Ordinal);
        Assert.Contains(Ola, mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("Forgot your password?", mail.TextBody, StringComparison.Ordinal);
        AssertNoLink(mail);
        Assert.Empty(await NoticesAsync());
    }

    [Fact]
    public async Task A_refused_link_queues_nothing()   // criterion 22: in the change's transaction, or not at all
    {
        await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());

        using var wrong = await ProviderApi.Link(Client, ticket, "Wrong-Passw0rd-9");

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Empty(await NoticesAsync());
        Assert.Null(await LimitAsync(MailKind.NoticeLimit));
    }

    [Fact]
    public async Task The_notices_are_not_counted_by_the_limit_of_spec_0004_and_do_not_count_against_it()   // criterion 22
    {
        await CreateUserAsync(Ola, confirmed: true);
        using (var forgot = await AccountApi.Forgot(Client, Ola))
        {
            Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);
        }

        Assert.True(await QueueAsync(MailKind.ProviderLinked));
        Assert.True(await QueueAsync(MailKind.PasswordChanged, provider: null));

        Assert.Equal(1, (await LimitAsync(MailKind.PasswordReset))!.WindowCount);
        Assert.Equal(2, (await LimitAsync(MailKind.NoticeLimit))!.WindowCount);
        Clock.Advance(PastTheLimit);
        using var again = await AccountApi.Forgot(Client, Ola);
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal(2, (await LimitAsync(MailKind.PasswordReset))!.WindowCount);
    }

    [Fact]
    public async Task The_eleventh_notice_in_an_hour_is_not_queued_the_change_still_happens_and_one_warning_names_the_kind()   // criterion 22
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        for (var i = 0; i < MailNotices.Rule.WindowLimit; i++)
        {
            Assert.True(await QueueAsync(i % 2 == 0 ? MailKind.ProviderUnlinked : MailKind.PasswordChanged, provider: i % 2 == 0 ? "github" : null));
        }

        var ticket = await TicketAsync("google", GooglePerson());
        using (var linked = await ProviderApi.Link(Client, ticket, UserPassword))
        {
            Assert.Equal(HttpStatusCode.OK, linked.StatusCode);   // the change happens
        }

        Assert.Single(await InDbAsync(db => db.UserLogins.Where(l => l.UserId == user.Id).ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(10, (await NoticesAsync()).Count);
        Assert.DoesNotContain(await NoticesAsync(), r => r.Kind == MailKind.ProviderLinked);
        Assert.Equal(1, LimitWarnings());
        var warning = Logs.Entries.Single(e => e.Message.Contains(LimitWarning, StringComparison.Ordinal)).Message;
        Assert.Contains(nameof(MailKind.ProviderLinked), warning, StringComparison.Ordinal);
        Assert.DoesNotContain(Ola, warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_window_is_fixed_an_hour_from_the_first_notice_with_no_gap_between_two()   // Amendment A10
    {
        await CreateUserAsync(Ola, confirmed: true);
        Assert.True(await QueueAsync(MailKind.PasswordChanged, provider: null));
        Clock.Advance(TimeSpan.FromMinutes(30));
        for (var i = 1; i < MailNotices.Rule.WindowLimit; i++)
        {
            Assert.True(await QueueAsync(MailKind.PasswordChanged, provider: null));   // no minimum gap
        }

        Assert.False(await QueueAsync(MailKind.PasswordChanged, provider: null));
        Clock.Advance(TimeSpan.FromMinutes(30));   // an hour after the first: a new window
        Assert.True(await QueueAsync(MailKind.PasswordChanged, provider: null));
        Assert.Equal(1, (await LimitAsync(MailKind.NoticeLimit))!.WindowCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_notice_goes_to_an_account_of_the_address_confirmed_or_not_and_never_as_a_verification(bool confirmed)   // criterion 22
    {
        var user = await CreateUserAsync(Ola, confirmed);
        Assert.True(await QueueAsync(MailKind.PasswordChanged, provider: null));

        Assert.Equal(1, await DispatchAsync());

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal(Ola, mail.To);
        AssertNoLink(mail);
        Assert.Equal(0, await InDbAsync(db => db.EmailTokens.CountAsync(t => t.UserId == user.Id, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_notice_whose_account_was_deleted_before_dispatch_is_dropped()   // criterion 22
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        Assert.True(await QueueAsync(MailKind.ProviderUnlinked, provider: "github"));
        await InDbAsync(db => db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync(TestContext.Current.CancellationToken));

        await DispatchAsync();

        Assert.Empty(Mail.Sent);
        Assert.Empty(await NoticesAsync());
    }

    [Fact]
    public async Task A_notice_is_mailed_to_the_address_as_the_account_stores_it_and_names_the_display_name()   // spec 0012 → Mails
    {
        await CreateUserAsync("Ola.Nowak@Firma.test", confirmed: true);
        Assert.True(await QueueAsync(MailKind.ProviderUnlinked, "OLA.NOWAK@FIRMA.TEST", "github"));

        await DispatchAsync();

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal("Ola.Nowak@Firma.test", mail.To);
        Assert.Contains("GitHub", mail.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("github", mail.TextBody, StringComparison.Ordinal);   // the display name, never the provider name
    }

    [Fact]
    public async Task A_finish_an_invitation_through_a_provider_and_a_reset_queue_no_notice()   // criterion 22
    {
        await PrivateThroughProviderAsync("google", GooglePerson());   // the finish

        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, "jan@firma.test", "user");
        await EnqueueInvitationAsync(invite, "jan@firma.test");
        await DispatchAsync();
        var inviteToken = TokenIn(Mail.Sent.Last(m => m.To == "jan@firma.test"));
        var invited = await TicketAsync("google", GooglePerson(email: "jan@firma.test", subject: "900000000000000000009"), "?invite=" + inviteToken);
        using (var accepted = await ProviderApi.Exchange(Client, invited))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        using (var forgot = await AccountApi.Forgot(Client, Ola))
        {
            Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);
        }

        await DispatchAsync();
        var resetToken = TokenIn(Mail.Sent.Last(m => m.To == Ola));
        using (var reset = await AccountApi.Reset(Client, resetToken, "Chosen-Passw0rd"))
        {
            Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        }

        Assert.Empty(await NoticesAsync());
        Assert.Null(await LimitAsync(MailKind.NoticeLimit));
    }

    [Fact]
    public async Task A_link_and_its_notice_leave_no_token_and_no_ticket_in_the_log()   // spec 0012 → General rules
    {
        await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());
        using (var linked = await ProviderApi.Link(Client, ticket, UserPassword))
        {
            Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        }

        await DispatchAsync();

        Assert.DoesNotContain(ticket.Value, Logs.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(ticket.Binding, Logs.Text, StringComparison.Ordinal);
        Assert.Empty(await InDbAsync(db => db.EmailTokens.ToListAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_provider_notice_row_without_a_provider_is_dropped_and_sends_nothing()   // the column is nullable; no endpoint writes such a row
    {
        await CreateUserAsync(Ola, confirmed: true);
        await InDbAsync(async db =>
        {
            var now = StorableTime.Now(Clock);
            db.MailRequests.Add(new MailRequest { Kind = MailKind.ProviderLinked, NormalizedEmail = OlaNormalized, RequestedAt = now, NextAttemptAt = now });
            return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        await DispatchAsync();

        Assert.Empty(Mail.Sent);
        Assert.Empty(await NoticesAsync());
    }

    [Fact]
    public async Task The_notice_limit_is_kept_per_address()   // the key is the hash of the address: ten for one address leave another's window alone
    {
        await CreateUserAsync(Ola, confirmed: true);
        for (var i = 0; i < MailNotices.Rule.WindowLimit; i++)
        {
            Assert.True(await QueueAsync(MailKind.PasswordChanged, provider: null));
        }

        Assert.False(await QueueAsync(MailKind.PasswordChanged, provider: null));
        Assert.True(await QueueAsync(MailKind.PasswordChanged, "OTHER@GMAIL.TEST", provider: null));
        Assert.Equal(MailNotices.Rule.WindowLimit, (await LimitAsync(MailKind.NoticeLimit))!.WindowCount);
    }

    [Fact]
    public async Task A_notice_with_a_provider_that_does_not_fit_its_kind_is_refused_before_anything_is_written()   // the caller's mistake, not a quiet row
    {
        await Assert.ThrowsAsync<ArgumentException>(() => QueueAsync(MailKind.ProviderLinked, provider: null));
        await Assert.ThrowsAsync<ArgumentException>(() => QueueAsync(MailKind.PasswordChanged, provider: "google"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => QueueAsync(MailKind.PasswordReset, provider: null));

        Assert.Null(await LimitAsync(MailKind.NoticeLimit));
        Assert.Empty(await NoticesAsync());
    }

    /// <summary>How long the dispatcher's signal takes to answer: at once when something notified it, the whole <paramref name="timeout"/> when not.</summary>
    private async Task<TimeSpan> WaitForSignalAsync(TimeSpan timeout)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        await Factory.Services.GetRequiredService<MailDispatchSignal>().WaitAsync(timeout, TimeProvider.System, TestContext.Current.CancellationToken);
        return started.Elapsed;
    }

    [Fact]
    public async Task A_committed_link_wakes_the_dispatcher_and_a_refused_one_does_not()   // Notify() runs after the commit, never for a change that did not happen
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());
        using (var wrong = await ProviderApi.Link(Client, ticket, "Wrong-Passw0rd-9"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        Assert.True(await WaitForSignalAsync(TimeSpan.FromMilliseconds(700)) >= TimeSpan.FromMilliseconds(500));   // nothing woke it

        using (var linked = await ProviderApi.Link(Client, ticket, UserPassword))
        {
            Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        }

        Assert.True(await WaitForSignalAsync(TimeSpan.FromSeconds(30)) < TimeSpan.FromSeconds(10));   // the commit woke it
        _ = user;
    }

    [Fact]
    public async Task A_connect_queues_provider_connected_and_a_connect_of_a_linked_identity_queues_nothing()   // criterion 22
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        using (var connected = await SettingsApi.Connect(Client, session, await TicketAsync("google", GooglePerson(), "?purpose=connect")))
        {
            Assert.Equal(HttpStatusCode.NoContent, connected.StatusCode);
        }

        var row = Assert.Single(await NoticesAsync());
        Assert.Equal((MailKind.ProviderLinked, OlaNormalized, "google"), (row.Kind, row.NormalizedEmail, row.Provider));
        Assert.Equal(row.RequestedAt, (await SingleAsync(AuditKinds.ProviderLinked)).OccurredAt);   // one transaction, one clock reading
        Assert.Equal(1, (await LimitAsync(MailKind.NoticeLimit))!.WindowCount);

        using (var again = await SettingsApi.Connect(Client, session, await TicketAsync("google", GooglePerson(), "?purpose=connect")))
        {
            Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        }

        Assert.Single(await NoticesAsync());
        Assert.Equal(1, (await LimitAsync(MailKind.NoticeLimit))!.WindowCount);   // the second connect counted nothing against the limit
        await DispatchAsync();
        var mail = Assert.Single(Mail.Sent);
        Assert.Equal(Ola, mail.To);
        Assert.Contains("Google", mail.Subject, StringComparison.Ordinal);
        AssertNoLink(mail);
        _ = user;
    }

    [Fact]
    public async Task A_refused_connect_queues_nothing_and_wakes_nothing_and_a_committed_one_wakes_the_dispatcher()   // criterion 22: in the change's transaction, woken after its commit
    {
        await CreateUserAsync(Ola, confirmed: true);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        var mismatched = await TicketAsync("google", GooglePerson(email: "ola.work@gmail.test", subject: "100000000000000000077"), "?purpose=connect");
        using (var refused = await SettingsApi.Connect(Client, session, mismatched))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        Assert.Empty(await NoticesAsync());
        Assert.Null(await LimitAsync(MailKind.NoticeLimit));
        Assert.True(await WaitForSignalAsync(TimeSpan.FromMilliseconds(700)) >= TimeSpan.FromMilliseconds(500));   // nothing woke it

        using (var connected = await SettingsApi.Connect(Client, session, await TicketAsync("google", GooglePerson(), "?purpose=connect")))
        {
            Assert.Equal(HttpStatusCode.NoContent, connected.StatusCode);
        }

        Assert.True(await WaitForSignalAsync(TimeSpan.FromSeconds(30)) < TimeSpan.FromSeconds(10));   // the commit woke it
    }

    [Fact]
    public async Task An_unlink_queues_provider_disconnected_naming_the_provider()   // criterion 22
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        await LinkAsync(user.Id, "github", "70000001");
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);

        using (var unlinked = await SettingsApi.Unlink(Client, session, "github"))
        {
            Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        }

        var row = Assert.Single(await NoticesAsync());
        Assert.Equal((MailKind.ProviderUnlinked, "github"), (row.Kind, row.Provider));
        await DispatchAsync();
        var mail = Assert.Single(Mail.Sent);
        Assert.Contains("GitHub", mail.Subject, StringComparison.Ordinal);
        Assert.Contains("disconnected", mail.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_unlink_queues_nothing_and_wakes_nothing_and_a_committed_one_wakes_the_dispatcher()   // criterion 22: in the change's transaction, woken after its commit
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        using (var refused = await SettingsApi.Unlink(Client, session, "microsoft"))
        {
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        }

        Assert.Empty(await NoticesAsync());
        Assert.Null(await LimitAsync(MailKind.NoticeLimit));
        Assert.True(await WaitForSignalAsync(TimeSpan.FromMilliseconds(700)) >= TimeSpan.FromMilliseconds(500));   // nothing woke it

        using (var unlinked = await SettingsApi.Unlink(Client, session, "google"))
        {
            Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        }

        Assert.True(await WaitForSignalAsync(TimeSpan.FromSeconds(30)) < TimeSpan.FromSeconds(10));   // the commit woke it
    }

    [Fact]
    public async Task An_unlink_beyond_the_notice_limit_still_happens_and_queues_no_row()   // Amendment A10: beyond it the change happens
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        for (var i = 0; i < 10; i++)
        {
            Assert.True(await QueueAsync(MailKind.PasswordChanged, provider: null));
        }

        using var unlinked = await SettingsApi.Unlink(Client, session, "google");

        Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        Assert.Equal(10, (await NoticesAsync()).Count);   // none for the unlink
        Assert.DoesNotContain(await NoticesAsync(), row => row.Kind == MailKind.ProviderUnlinked);
        Assert.Equal(1, LimitWarnings());
        Assert.Empty(await InDbAsync(db => db.UserLogins.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken)));
    }
}
