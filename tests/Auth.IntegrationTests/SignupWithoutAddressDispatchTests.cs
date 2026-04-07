using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Signup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests;

/// <summary>
/// The manifest says <c>signup: self-service</c> but the completion address is gone (spec 0009 → General rules: sign-up is off then). A request
/// that was queued while the address was set must not reach the composer, which has no address to put into a link: it is dropped quietly, not
/// retried as a failed send.
/// </summary>
public sealed class SignupWithoutAddressDispatchTests : SignupTestBase
{
    public SignupWithoutAddressDispatchTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithSetting(MailSettingsLoader.CompleteSignupUrlKey, "");
    }

    [Fact]
    public async Task A_queued_sign_up_request_is_dropped_without_a_failed_send_when_the_completion_address_is_not_set()
    {
        Assert.False(Factory.Services.GetRequiredService<SignupGate>().IsOn);

        // What a request made while the address was set had left: a live pending sign-up and its queued request.
        await InDbAsync(async db =>
        {
            var now = StorableTime.Now(Clock);
            db.PendingSignups.Add(new PendingSignup
            {
                NormalizedEmail = "ANNA@FIRMA.TEST",
                Email = Address,
                CompanyName = CompanyName,
                RequestedAt = now,
                ExpiresAt = now + TimeSpan.FromHours(24),
            });
            db.MailRequests.Add(new MailRequest { Kind = MailKind.Signup, NormalizedEmail = "ANNA@FIRMA.TEST", RequestedAt = now, NextAttemptAt = now });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return 0;
        });

        var mark = Logs.Entries.Count;
        Assert.Equal(1, await DispatchAsync());

        Assert.Empty(Mail.Attempted);
        Assert.Equal(0, await QueueCountAsync());
        Assert.DoesNotContain(Logs.Entries.Skip(mark), e => e.Level >= LogLevel.Warning);
    }
}
