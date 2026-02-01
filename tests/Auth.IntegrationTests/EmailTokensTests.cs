using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class EmailTokensTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    private DateTimeOffset Now => StorableTime.Now(Clock);

    private async Task<T> InDbAsync<T>(Func<AuthDbContext, Task<T>> work)
    {
        using var scope = Factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    private Task<string> IssueAsync(Guid user, MailKind kind) =>
        InDbAsync(db => EmailTokens.IssueAsync(db, user, kind, Now, TestContext.Current.CancellationToken));

    private Task<Guid?> ConsumeAsync(string token, MailKind kind) =>
        InDbAsync(db => EmailTokens.ConsumeAsync(db, token, kind, Now, TestContext.Current.CancellationToken));

    private Task<List<EmailToken>> RowsAsync() =>
        InDbAsync(db => db.EmailTokens.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task Issued_token_is_43_url_safe_characters_and_only_its_hash_is_stored()   // criterion 15
    {
        var user = await Factory.SeedUserIdAsync();

        var token = await IssueAsync(user, MailKind.PasswordReset);

        Assert.Matches("^[A-Za-z0-9_-]{43}$", token);
        var row = Assert.Single(await RowsAsync());
        Assert.Equal(EmailTokens.HashOf(token), row.TokenHash);
        Assert.Equal(32, row.TokenHash.Length);
        Assert.Equal(user, row.UserId);
        Assert.Equal(Now + TimeSpan.FromHours(1), row.ExpiresAt);
    }

    [Fact]
    public async Task Token_works_once()   // criterion 6
    {
        var user = await Factory.SeedUserIdAsync();
        var token = await IssueAsync(user, MailKind.PasswordReset);

        Assert.Equal(user, await ConsumeAsync(token, MailKind.PasswordReset));
        Assert.Null(await ConsumeAsync(token, MailKind.PasswordReset));
        Assert.Empty(await RowsAsync());
    }

    [Fact]
    public async Task Unknown_token_is_not_usable() =>
        Assert.Null(await ConsumeAsync("never-issued", MailKind.PasswordReset));

    [Fact]
    public async Task Token_of_the_other_kind_is_not_usable_and_is_left_alone()
    {
        var user = await Factory.SeedUserIdAsync();
        var token = await IssueAsync(user, MailKind.PasswordReset);

        Assert.Null(await ConsumeAsync(token, MailKind.EmailVerification));
        Assert.Equal(user, await ConsumeAsync(token, MailKind.PasswordReset));
    }

    [Theory]
    [InlineData(MailKind.PasswordReset, 1)]
    [InlineData(MailKind.EmailVerification, 24)]
    public async Task Token_expires_after_its_lifetime(MailKind kind, int hours)   // criterion 6
    {
        var user = await Factory.SeedUserIdAsync();
        var early = await IssueAsync(user, kind);
        Clock.Advance(TimeSpan.FromHours(hours) - TimeSpan.FromSeconds(1));
        Assert.Equal(user, await ConsumeAsync(early, kind));

        var late = await IssueAsync(user, kind);
        Clock.Advance(TimeSpan.FromHours(hours));
        Assert.Null(await ConsumeAsync(late, kind));
    }

    [Fact]
    public async Task Issuing_again_replaces_the_earlier_token_of_that_kind_only()   // criterion 6
    {
        var user = await Factory.SeedUserIdAsync();
        var firstReset = await IssueAsync(user, MailKind.PasswordReset);
        var verification = await IssueAsync(user, MailKind.EmailVerification);

        var secondReset = await IssueAsync(user, MailKind.PasswordReset);

        Assert.Equal(2, (await RowsAsync()).Count);
        Assert.Null(await ConsumeAsync(firstReset, MailKind.PasswordReset));
        Assert.Equal(user, await ConsumeAsync(secondReset, MailKind.PasswordReset));
        Assert.Equal(user, await ConsumeAsync(verification, MailKind.EmailVerification));
    }

    [Fact]
    public async Task Parallel_consumption_succeeds_exactly_once()   // criterion 6
    {
        var user = await Factory.SeedUserIdAsync();
        var token = await IssueAsync(user, MailKind.PasswordReset);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => ConsumeAsync(token, MailKind.PasswordReset))));

        Assert.Equal(1, results.Count(r => r == user));
        Assert.Equal(7, results.Count(r => r is null));
    }

    [Fact]
    public async Task Tokens_are_removed_with_their_user()
    {
        Guid id;
        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = "gone@example.com", Email = "gone@example.com" };
            Assert.True((await users.CreateAsync(user, "Short-Lived-1")).Succeeded);
            id = user.Id;
            await IssueAsync(id, MailKind.EmailVerification);
            Assert.True((await users.DeleteAsync(user)).Succeeded);
        }

        Assert.Empty(await RowsAsync());
    }
}
