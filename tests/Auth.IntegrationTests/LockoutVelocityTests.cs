using System.Net;
using Auth.IntegrationTests.Infrastructure;

namespace Auth.IntegrationTests;

public sealed class LockoutVelocityTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    private async Task FailFastAsync(string email, int times)
    {
        for (var i = 0; i < times; i++)
        {
            using var response = await LoginApi.Login(Client, email, LockoutApi.WrongPassword);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Five_failures_within_ten_seconds_lock_the_sixth_attempt()   // criterion 5
    {
        await FailFastAsync(Factory.SeedEmail, 5);

        using var sixth = await LoginApi.Login(Client, Factory.SeedEmail, Factory.SeedPassword);

        Assert.Equal(120, await LockoutApi.AssertLockedAsync(sixth));   // base 1 min + this attempt's 1 min
    }

    [Fact]
    public async Task Five_failures_at_a_human_pace_do_not_lock()   // criterion 5
    {
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 5);
        Clock.Advance(LockoutApi.HumanPace);

        await LoginApi.LoginOk(Client, Factory.SeedEmail, Factory.SeedPassword);
    }

    [Fact]
    public async Task Fifth_attempt_of_a_burst_with_the_correct_password_logs_in()
    {
        await FailFastAsync(Factory.SeedEmail, 4);

        await LoginApi.LoginOk(Client, Factory.SeedEmail, Factory.SeedPassword);
    }

    [Fact]
    public async Task Successful_login_closes_the_window()   // Decision 9
    {
        await FailFastAsync(Factory.SeedEmail, 4);
        await LoginApi.LoginOk(Client, Factory.SeedEmail, Factory.SeedPassword);

        // Had the window survived the success, the first of these would be its sixth attempt.
        await FailFastAsync(Factory.SeedEmail, 4);
        await LoginApi.LoginOk(Client, Factory.SeedEmail, Factory.SeedPassword);
    }

    [Fact]
    public async Task Closed_window_starts_again()
    {
        await FailFastAsync(Factory.SeedEmail, 4);
        Clock.Advance(TimeSpan.FromSeconds(10));
        await FailFastAsync(Factory.SeedEmail, 4);   // eight failures, never five in one window

        Clock.Advance(TimeSpan.FromSeconds(10));
        await LoginApi.LoginOk(Client, Factory.SeedEmail, Factory.SeedPassword);
    }

    [Fact]
    public async Task Repeated_successful_logins_never_trip_the_rule()   // Decision 9
    {
        for (var i = 0; i < 8; i++)
        {
            await LoginApi.LoginOk(Client, Factory.SeedEmail, Factory.SeedPassword);
        }
    }

    [Fact]
    public async Task Burst_on_an_unknown_email_locks_the_same_way()   // criteria 4, 8
    {
        await FailFastAsync(Factory.SeedEmail, 5);
        await FailFastAsync("nobody@example.com", 5);

        using var existing = await LoginApi.Login(Client, Factory.SeedEmail, LockoutApi.WrongPassword);
        using var unknown = await LoginApi.Login(Client, "nobody@example.com", LockoutApi.WrongPassword);

        await LockoutApi.AssertLockedAsync(existing);
        Assert.Equal(await existing.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        Assert.Equal(LoginApi.HeaderNames(existing), LoginApi.HeaderNames(unknown));
    }

    [Fact]
    public async Task Parallel_failures_buy_at_most_five_password_evaluations()   // Review Focus 1, Decision 14
    {
        // Twelve, not more: see LoginStreakStoreTests on the connections a parallel test holds.
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            Task.Run(() => LoginApi.Login(Client, Factory.SeedEmail, LockoutApi.WrongPassword))));

        try
        {
            // A 401 is the only outcome of an evaluated wrong password; everything else must be the lockout response.
            Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));
            Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }
}
