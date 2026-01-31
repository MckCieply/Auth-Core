using System.Net;
using Auth.Infrastructure.Identity;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Seeding;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class UnverifiedLoginTests(PostgresFixture postgres, KeyMaterialFixture keys) : MailTestBase(postgres, keys)
{
    private const string Email = "new@example.com";
    private const string NotVerified = """{"error":"email_not_verified"}""";

    private static async Task AssertNotVerifiedAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"Expected 403, got {(int)response.StatusCode}: {raw}");
        Assert.Equal(NotVerified, raw);
        AccountApi.AssertNeverStoredAndNoCookie(response);
    }

    private Task<int> StreakCountAsync() =>
        InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task Correct_password_for_an_unconfirmed_account_is_refused_with_its_own_answer()   // criterion 13
    {
        await CreateUserAsync(Email, confirmed: false);

        using var response = await LoginApi.Login(Client, Email, UserPassword);

        await AssertNotVerifiedAsync(response);
    }

    [Fact]
    public async Task Wrong_password_for_an_unconfirmed_account_is_the_ordinary_401()   // criterion 13
    {
        await CreateUserAsync(Email, confirmed: false);

        using var unconfirmed = await LoginApi.Login(Client, Email, LockoutApi.WrongPassword);
        using var confirmed = await LoginApi.Login(Client, Factory.SeedEmail, LockoutApi.WrongPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, unconfirmed.StatusCode);
        Assert.Equal(await confirmed.Content.ReadAsStringAsync(), await unconfirmed.Content.ReadAsStringAsync());
        Assert.Equal(LoginApi.HeaderNames(confirmed), LoginApi.HeaderNames(unconfirmed));
    }

    [Fact]
    public async Task Refusal_ends_the_streak_as_a_success_would()   // criterion 13
    {
        await CreateUserAsync(Email, confirmed: false);
        await LockoutApi.FailAsync(Client, Clock, Email, 9);

        Clock.Advance(LockoutApi.HumanPace);
        using (var refused = await LoginApi.Login(Client, Email, UserPassword))   // the tenth attempt of the streak
        {
            await AssertNotVerifiedAsync(refused);
        }

        Assert.Equal(0, await StreakCountAsync());
        await LockoutApi.FailAsync(Client, Clock, Email, 9);   // nine more ordinary 401s: no lock
    }

    [Fact]
    public async Task During_a_cooldown_the_answer_is_the_lockout_not_the_refusal()   // criterion 13
    {
        await CreateUserAsync(Email, confirmed: false);
        await LockoutApi.FailAsync(Client, Clock, Email, 10);

        using var response = await LoginApi.Login(Client, Email, UserPassword);

        await LockoutApi.AssertLockedAsync(response);
    }

    [Fact]
    public async Task After_verification_the_account_logs_in()   // criterion 12
    {
        await CreateUserAsync(Email, confirmed: false);
        using (var refused = await LoginApi.Login(Client, Email, UserPassword))
        {
            await AssertNotVerifiedAsync(refused);
        }

        using (var requested = await AccountApi.RequestVerification(Client, Email))
        {
            await AccountApi.AssertEmptyAsync(requested, HttpStatusCode.Accepted);
        }

        await DispatchAsync();
        using (var verified = await AccountApi.Verify(Client, TokenIn(Assert.Single(Mail.Sent))))
        {
            await AccountApi.AssertEmptyAsync(verified, HttpStatusCode.NoContent);
        }

        Clock.Advance(LockoutApi.HumanPace);
        using var login = await LoginApi.Login(Client, Email, UserPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        _ = SessionApi.RefreshCookieOf(login);
    }

    [Fact]
    public async Task Confirmed_accounts_log_in_as_before()
    {
        using var login = await LoginApi.Login(Client, Factory.SeedEmail, Factory.SeedPassword);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Second_seed_user_is_created_unconfirmed_and_only_once()
    {
        var database = "auth_" + Guid.NewGuid().ToString("N");
        await using var first = new AuthAppFactory(Postgres, Keys, database)
            .WithSetting(DevUserSeeder.UnverifiedEmailKey, Email)
            .WithSetting(DevUserSeeder.UnverifiedPasswordKey, UserPassword);
        using (var client = first.CreateClient())
        {
            using var response = await LoginApi.Login(client, Email, UserPassword);
            await AssertNotVerifiedAsync(response);
        }

        await using var second = new AuthAppFactory(Postgres, Keys, database)   // a restart
            .WithSetting(DevUserSeeder.UnverifiedEmailKey, Email)
            .WithSetting(DevUserSeeder.UnverifiedPasswordKey, "Different-Passw0rd");
        using var scope = second.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(Email);
        Assert.NotNull(user);
        Assert.False(user.EmailConfirmed);
        Assert.True(await users.CheckPasswordAsync(user, UserPassword));          // never reset by the seed
        Assert.True((await users.FindByEmailAsync(second.SeedEmail))!.EmailConfirmed);
    }

    [Fact]
    public async Task Production_host_does_not_seed_the_second_user()
    {
        await using var factory = new AuthAppFactory(Postgres, Keys)
            .WithEnvironment("Production")
            .WithSetting(DevUserSeeder.UnverifiedEmailKey, Email)
            .WithSetting(DevUserSeeder.UnverifiedPasswordKey, UserPassword);

        using var scope = factory.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(Email));
    }

    [Fact]
    public async Task Without_the_settings_there_is_no_second_user() =>
        Assert.Equal(1, await InDbAsync(db => db.Users.CountAsync(TestContext.Current.CancellationToken)));
}
