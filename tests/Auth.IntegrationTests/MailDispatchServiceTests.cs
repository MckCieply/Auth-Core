using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Auth.IntegrationTests;

public sealed class MailDispatchServiceTests : SessionTestBase
{
    private readonly CapturingMailTransport _mail = new();

    public MailDispatchServiceTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithServices(services => services.Replace(ServiceDescriptor.Singleton<IMailTransport>(_mail)));
    }

    private async Task RequestResetAsync()
    {
        var decision = await Factory.Services.GetRequiredService<MailRequestStore>()
            .SubmitAsync(MailKind.PasswordReset, Factory.SeedEmail.ToUpperInvariant(), TestContext.Current.CancellationToken);
        Assert.True(decision.Allowed);
        Factory.Services.GetRequiredService<MailDispatchSignal>().Notify();
    }

    [Fact]
    public async Task Queued_request_is_sent_without_anyone_driving_the_dispatcher()
    {
        await RequestResetAsync();

        await Poll.UntilAsync(() => _mail.Sent.Count == 1, "the mail to be sent by the background service");
    }

    [Fact]
    public async Task Failed_send_is_retried_once_its_delay_has_passed_on_the_clock()   // criterion 14
    {
        _mail.Failing = true;
        await RequestResetAsync();
        await Poll.UntilAsync(() => _mail.Attempts == 1, "the first, failing attempt");

        _mail.Failing = false;

        // The loop may not have started its wait yet: keep moving the clock until the retry has happened. At most
        // 200 steps of 5 s, well under the hour after which the request would be dropped.
        await Poll.UntilAsync(
            () =>
            {
                Clock.Advance(TimeSpan.FromSeconds(5));
                return _mail.Sent.Count == 1;
            },
            "the retry after the delay");
    }

    [Fact]
    public async Task Request_that_arrives_while_the_loop_waits_wakes_it()
    {
        await RequestResetAsync();
        await Poll.UntilAsync(() => _mail.Sent.Count == 1, "the first mail");

        // The queue is empty now and the loop waits a minute of a clock that does not move: only the signal can wake it.
        Clock.Advance(TimeSpan.FromSeconds(61));   // past the mail limit; fires the wait once, which finds nothing
        await RequestResetAsync();

        await Poll.UntilAsync(() => _mail.Sent.Count == 2, "the second mail");
    }
}
