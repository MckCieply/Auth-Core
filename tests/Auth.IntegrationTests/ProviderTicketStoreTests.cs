using System.Security.Cryptography;
using System.Text;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → Provider tickets: what the store keeps, finds and uses up. The endpoints' answers are in <c>ProviderTicketTests</c>.</summary>
public sealed class ProviderTicketStoreTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    private static readonly ProviderIdentity Ola = new("google", "100000000000000000001", "ola@gmail.test", true);

    private async Task<T> InDbAsync<T>(Func<AuthDbContext, Task<T>> work)
    {
        using var scope = Factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    private Task<IssuedTicket> IssueAsync(ProviderTicketPurpose purpose, byte[]? inviteHash = null) =>
        InDbAsync(db => ProviderTickets.IssueAsync(db, new ProviderTicketDraft(purpose, Ola, inviteHash), StorableTime.Now(Clock), TestContext.Current.CancellationToken));

    private static byte[] Sha256(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Issuing_stores_the_hashes_only_and_the_expiry_of_the_purpose()
    {
        var now = StorableTime.Now(Clock);
        var invite = Sha256("an-invitation-token");

        var signIn = await IssueAsync(ProviderTicketPurpose.SignIn);
        var reauth = await IssueAsync(ProviderTicketPurpose.Reauth);
        var invited = await IssueAsync(ProviderTicketPurpose.Invite, invite);

        var rows = await InDbAsync(db => db.ProviderTickets.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, rows.Count);
        foreach (var issued in new[] { signIn, reauth, invited })
        {
            Assert.Equal(43, issued.Ticket.Length);
            Assert.Equal(43, issued.Binding.Length);
            Assert.NotEqual(issued.Ticket, issued.Binding);
            var row = Assert.Single(rows, r => r.TicketHash.AsSpan().SequenceEqual(Sha256(issued.Ticket)));
            Assert.Equal(Sha256(issued.Binding), row.BindingHash);
            Assert.Equal("google", row.Provider);
            Assert.Equal("100000000000000000001", row.IdentityKey);
            Assert.Equal("ola@gmail.test", row.Email);
            Assert.True(row.EmailVerified);
        }

        Assert.Equal(now + TimeSpan.FromMinutes(10), rows.Single(r => r.Purpose == ProviderTicketPurpose.SignIn).ExpiresAt);
        Assert.Equal(now + TimeSpan.FromMinutes(5), rows.Single(r => r.Purpose == ProviderTicketPurpose.Reauth).ExpiresAt);
        Assert.Equal(invite, rows.Single(r => r.Purpose == ProviderTicketPurpose.Invite).InviteTokenHash);
        Assert.Null(rows.Single(r => r.Purpose == ProviderTicketPurpose.SignIn).InviteTokenHash);
    }

    [Fact]
    public async Task A_ticket_is_found_only_with_its_binding_and_before_its_expiry()
    {
        var issued = await IssueAsync(ProviderTicketPurpose.SignIn);
        var other = await IssueAsync(ProviderTicketPurpose.SignIn);
        var cancellation = TestContext.Current.CancellationToken;

        Assert.NotNull(await InDbAsync(db => ProviderTickets.FindAsync(db, issued.Ticket, issued.Binding, StorableTime.Now(Clock), cancellation)));
        Assert.Null(await InDbAsync(db => ProviderTickets.FindAsync(db, issued.Ticket, null, StorableTime.Now(Clock), cancellation)));
        Assert.Null(await InDbAsync(db => ProviderTickets.FindAsync(db, issued.Ticket, other.Binding, StorableTime.Now(Clock), cancellation)));
        Assert.Null(await InDbAsync(db => ProviderTickets.FindAsync(db, "unknown-ticket", issued.Binding, StorableTime.Now(Clock), cancellation)));

        Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Null(await InDbAsync(db => ProviderTickets.FindAsync(db, issued.Ticket, issued.Binding, StorableTime.Now(Clock), cancellation)));
    }

    [Fact]
    public async Task A_ticket_is_used_up_once_and_only_for_its_purpose()
    {
        var issued = await IssueAsync(ProviderTicketPurpose.SignIn);
        var cancellation = TestContext.Current.CancellationToken;

        Assert.Null(await InDbAsync(db => ProviderTickets.ConsumeAsync(db, issued.Ticket, issued.Binding, ProviderTicketPurpose.Reauth, StorableTime.Now(Clock), cancellation)));
        var used = await InDbAsync(db => ProviderTickets.ConsumeAsync(db, issued.Ticket, issued.Binding, ProviderTicketPurpose.SignIn, StorableTime.Now(Clock), cancellation));
        var again = await InDbAsync(db => ProviderTickets.ConsumeAsync(db, issued.Ticket, issued.Binding, ProviderTicketPurpose.SignIn, StorableTime.Now(Clock), cancellation));

        Assert.NotNull(used);
        Assert.Equal("ola@gmail.test", used.Email);
        Assert.Null(again);
        Assert.Equal(0, await InDbAsync(db => db.ProviderTickets.CountAsync(cancellation)));
    }

    [Fact]
    public async Task A_ticket_is_not_used_up_by_another_browsers_binding_or_after_its_expiry()
    {
        var issued = await IssueAsync(ProviderTicketPurpose.SignIn);
        var other = await IssueAsync(ProviderTicketPurpose.SignIn);
        var cancellation = TestContext.Current.CancellationToken;

        Assert.Null(await InDbAsync(db => ProviderTickets.ConsumeAsync(db, issued.Ticket, other.Binding, ProviderTicketPurpose.SignIn, StorableTime.Now(Clock), cancellation)));
        Assert.Equal(2, await InDbAsync(db => db.ProviderTickets.CountAsync(cancellation)));

        Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Null(await InDbAsync(db => ProviderTickets.ConsumeAsync(db, issued.Ticket, issued.Binding, ProviderTicketPurpose.SignIn, StorableTime.Now(Clock), cancellation)));
        Assert.Equal(2, await InDbAsync(db => db.ProviderTickets.CountAsync(cancellation)));
    }

    [Fact]
    public async Task Of_parallel_requests_with_one_ticket_exactly_one_gets_it()
    {
        var issued = await IssueAsync(ProviderTicketPurpose.SignIn);
        var cancellation = TestContext.Current.CancellationToken;
        var now = StorableTime.Now(Clock);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            InDbAsync(db => ProviderTickets.ConsumeAsync(db, issued.Ticket, issued.Binding, ProviderTicketPurpose.SignIn, now, cancellation))));

        Assert.Equal(1, results.Count(r => r is not null));
        Assert.Equal(0, await InDbAsync(db => db.ProviderTickets.CountAsync(cancellation)));
    }

    [Fact]
    public async Task The_hourly_pass_deletes_the_expired_tickets_and_keeps_the_others()   // spec 0011 → Provider tickets: the clean-up of spec 0004
    {
        await IssueAsync(ProviderTicketPurpose.Reauth);   // 5 minutes
        await IssueAsync(ProviderTicketPurpose.SignIn);   // 10 minutes
        Clock.Advance(TimeSpan.FromMinutes(6));

        var pruned = await Factory.Services.GetRequiredService<EmailPruner>().PruneProviderTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, pruned);
        var left = await InDbAsync(db => db.ProviderTickets.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ProviderTicketPurpose.SignIn, left.Purpose);
    }
}
