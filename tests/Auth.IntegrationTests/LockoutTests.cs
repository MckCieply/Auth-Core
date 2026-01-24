using System.Net;
using System.Net.Http.Json;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class LockoutTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    private const string UnknownEmail = "nobody@example.com";

    private Task<HttpResponseMessage> Wrong(string email) => LoginApi.Login(Client, email, LockoutApi.WrongPassword);

    private Task<HttpResponseMessage> Correct() => LoginApi.Login(Client, Factory.SeedEmail, Factory.SeedPassword);

    [Fact]
    public async Task Tenth_failure_locks_and_the_correct_password_is_then_refused()   // criterion 1
    {
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 10);

        using var refused = await Correct();

        // The 1-minute cooldown, plus the minute this attempt adds.
        Assert.Equal(120, await LockoutApi.AssertLockedAsync(refused));
    }

    [Fact]
    public async Task Lockout_response_carries_the_error_and_the_remaining_cooldown()   // criterion 2
    {
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 10);
        Clock.Advance(TimeSpan.FromSeconds(20.5));

        using var refused = await Wrong(Factory.SeedEmail);

        // 39.5 s were left; this attempt adds 60 s; rounded up.
        Assert.Equal(100, await LockoutApi.AssertLockedAsync(refused));
    }

    [Fact]
    public async Task Every_attempt_while_locked_adds_one_minute_whatever_the_password()   // criterion 3, Decision 7
    {
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 10);

        using var wrong = await Wrong(Factory.SeedEmail);
        using var correct = await Correct();
        using var wrongAgain = await Wrong(Factory.SeedEmail);

        int[] retries =
        [
            await LockoutApi.AssertLockedAsync(wrong),
            await LockoutApi.AssertLockedAsync(correct),
            await LockoutApi.AssertLockedAsync(wrongAgain),
        ];
        Assert.Equal([120, 180, 240], retries);
    }

    [Fact]
    public async Task Cooldown_never_exceeds_thirty_minutes()   // criterion 3
    {
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 10);

        var last = 0;
        for (var i = 0; i < 40; i++)
        {
            using var refused = await (i % 2 == 0 ? Wrong(Factory.SeedEmail) : Correct());
            var seconds = await LockoutApi.AssertLockedAsync(refused);
            Assert.InRange(seconds, last, 1800);
            last = seconds;
        }

        Assert.Equal(1800, last);
    }

    [Fact]
    public async Task Unknown_and_existing_email_get_the_same_lockout_response()   // criteria 4, 8
    {
        for (var i = 0; i < 10; i++)
        {
            Clock.Advance(LockoutApi.HumanPace);
            using var existingFailure = await Wrong(Factory.SeedEmail);
            using var unknownFailure = await Wrong(UnknownEmail);
            Assert.Equal(HttpStatusCode.Unauthorized, existingFailure.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, unknownFailure.StatusCode);
        }

        using var existing = await Wrong(Factory.SeedEmail);
        using var unknown = await Wrong(UnknownEmail);

        await LockoutApi.AssertLockedAsync(existing);
        await LockoutApi.AssertLockedAsync(unknown);
        Assert.Equal(await existing.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        Assert.Equal(existing.Content.Headers.ContentType, unknown.Content.Headers.ContentType);
        Assert.Equal(existing.Headers.RetryAfter, unknown.Headers.RetryAfter);
        Assert.Equal(LoginApi.HeaderNames(existing), LoginApi.HeaderNames(unknown));
    }

    [Fact]
    public async Task After_the_cooldown_a_correct_login_succeeds_and_resets_the_streak()   // criterion 7
    {
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 10);
        Clock.Advance(TimeSpan.FromSeconds(61));

        await LoginApi.LoginOk(Client, Factory.SeedEmail, Factory.SeedPassword);

        // Had the streak survived, the first of these would start a cooldown and the second would be refused.
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 9);
        Clock.Advance(LockoutApi.HumanPace);
        await LoginApi.LoginOk(Client, Factory.SeedEmail, Factory.SeedPassword);
    }

    [Fact]
    public async Task Successful_login_resets_the_streak_before_any_lock()   // criterion 7
    {
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 9);
        Clock.Advance(LockoutApi.HumanPace);
        await LoginApi.LoginOk(Client, Factory.SeedEmail, Factory.SeedPassword);   // the tenth attempt, and it is correct

        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 9);
        Clock.Advance(LockoutApi.HumanPace);
        await LoginApi.LoginOk(Client, Factory.SeedEmail, Factory.SeedPassword);
    }

    [Fact]
    public async Task Failure_after_an_expired_cooldown_starts_a_longer_one()   // criterion 9
    {
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 10);   // cooldown: 1 min
        Clock.Advance(TimeSpan.FromSeconds(61));

        using var eleventh = await Wrong(Factory.SeedEmail);                // evaluated; starts 2 min
        using var twelfth = await Wrong(Factory.SeedEmail);                 // refused; 2 min + 1 min
        Assert.Equal(HttpStatusCode.Unauthorized, eleventh.StatusCode);
        Assert.Equal(180, await LockoutApi.AssertLockedAsync(twelfth));

        Clock.Advance(TimeSpan.FromSeconds(181));
        using var thirteenth = await Wrong(Factory.SeedEmail);              // evaluated; starts 4 min
        using var fourteenth = await Wrong(Factory.SeedEmail);              // refused; 4 min + 1 min
        Assert.Equal(HttpStatusCode.Unauthorized, thirteenth.StatusCode);
        Assert.Equal(300, await LockoutApi.AssertLockedAsync(fourteenth));
    }

    [Fact]
    public async Task Streak_is_forgotten_after_24_hours_without_an_attempt()   // criterion 10
    {
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 9);
        Clock.Advance(TimeSpan.FromHours(24));

        // Nine more: with the old streak still counted, the first would lock and the second would get a 429.
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 9);
        Clock.Advance(LockoutApi.HumanPace);
        await LoginApi.LoginOk(Client, Factory.SeedEmail, Factory.SeedPassword);
    }

    [Fact]
    public async Task Spelling_variants_of_one_email_share_a_streak()   // Review Focus 2
    {
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail.ToLowerInvariant(), 5);
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail.ToUpperInvariant(), 5);

        using var refused = await LoginApi.Login(Client, "User@Example.Com", Factory.SeedPassword);

        await LockoutApi.AssertLockedAsync(refused);
    }

    [Fact]
    public async Task Malformed_request_is_not_an_attempt()   // Review Focus 3
    {
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 9);

        for (var i = 0; i < 3; i++)
        {
            Clock.Advance(LockoutApi.HumanPace);
            using var malformed = await Client.PostAsJsonAsync(LoginApi.Path, new { email = Factory.SeedEmail });
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        }

        // Had the 400s counted, the streak would be at 12 and locked.
        Clock.Advance(LockoutApi.HumanPace);
        await LoginApi.LoginOk(Client, Factory.SeedEmail, Factory.SeedPassword);
    }

    public static TheoryData<string> HostileEmails() => new()
    {
        new string('a', 7000) + "@example.com",
        "ÜSER@exämple.com",
        "  spaced@example.com  ",
        "emoji-\U0001F600@example.com",
    };

    [Theory]
    [MemberData(nameof(HostileEmails))]
    public async Task Hostile_email_is_counted_and_locked_like_any_other(string email)   // Review Focus 4
    {
        await LockoutApi.FailAsync(Client, Clock, email, 10);

        using var refused = await Wrong(email);

        Assert.Equal(120, await LockoutApi.AssertLockedAsync(refused));

        // Whatever was submitted, what is stored is one row with a 32-byte key.
        using var scope = Factory.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<AuthDbContext>().LoginStreaks
            .AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(32, row.IdentifierHash.Length);
    }

    [Fact]
    public async Task Cooldown_survives_a_host_restart()   // Review Focus 5
    {
        await using (var before = new AuthAppFactory(Postgres, Keys, Factory.DatabaseName).WithClock(Clock))
        {
            using var client = SessionApi.CreateClient(before);
            await LockoutApi.FailAsync(client, Clock, before.SeedEmail, 10);
        }

        await using var after = new AuthAppFactory(Postgres, Keys, Factory.DatabaseName).WithClock(Clock);
        using var restarted = SessionApi.CreateClient(after);
        using var refused = await LoginApi.Login(restarted, after.SeedEmail, after.SeedPassword);

        await LockoutApi.AssertLockedAsync(refused);
    }
}
