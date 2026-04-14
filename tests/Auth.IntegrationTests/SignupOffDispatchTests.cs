using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Signup;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests;

/// <summary>
/// An instance that has sign-up off sends no sign-up mail (spec 0009 → General rules): a request that was queued while sign-up was on is dropped
/// when it comes up, whatever its address has (a pending sign-up, an account with a company, a private account), and nothing is logged as a
/// failure.
/// </summary>
public sealed class SignupOffDispatchTests(PostgresFixture postgres, KeyMaterialFixture keys) : SignupTestBase(postgres, keys)
{
    /// <summary>The SQL of every database command the host has logged since <paramref name="mark"/> log entries.</summary>
    private List<string> StatementsSince(int mark) =>
        [.. Logs.Entries.Skip(mark)
            .Where(e => e.Category == "Microsoft.EntityFrameworkCore.Database.Command")
            .Select(e => e.Message[(e.Message.IndexOf('\n', StringComparison.Ordinal) + 1)..].Trim())];

    private void TurnSignUpOff()
    {
        Holder.Set(Manifest.BuiltIn, null);   // the manifest of an instance that does not offer sign-up
        Assert.False(Factory.Services.GetRequiredService<SignupGate>().IsOn);
    }

    [Theory]
    [InlineData("new address")]
    [InlineData("account with a company")]
    [InlineData("private account")]
    public async Task A_sign_up_request_queued_before_sign_up_was_turned_off_sends_nothing_and_is_dropped(string address)
    {
        if (address == "account with a company")
        {
            await CreateUserAsync(Address, confirmed: true);
        }
        else if (address == "private account")
        {
            await CreatePrivateAccountAsync(Address);
        }

        using var response = await SignupApi.Request(Client, Address, CompanyName);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        TurnSignUpOff();

        var mark = Logs.Entries.Count;
        Assert.Equal(1, await DispatchAsync());

        Assert.Empty(Mail.Sent);
        Assert.Empty(Mail.Attempted);
        Assert.Equal(0, await QueueCountAsync());
        Assert.DoesNotContain(Logs.Entries.Skip(mark), e => e.Level >= LogLevel.Warning);
        // The bulk delete took it: the single-row path would have locked the pending sign-up first.
        Assert.DoesNotContain(StatementsSince(mark), sql => sql.StartsWith("SELECT * FROM \"PendingSignups\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_sign_up_mail_that_comes_up_after_sign_up_was_turned_off_during_the_pass_is_dropped()   // the single-row path looks at the gate too
    {
        using (var first = await SignupApi.Request(Client, "first@firma.test", CompanyName))
        using (var second = await SignupApi.Request(Client, "second@firma.test", CompanyName))
        {
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        }

        // Both are live when the pass starts; sign-up is turned off while the first mail is being sent.
        Mail.DuringSend = () =>
        {
            TurnSignUpOff();
            return Task.CompletedTask;
        };
        var mark = Logs.Entries.Count;

        Assert.Equal(2, await DispatchAsync());

        Assert.Equal("first@firma.test", Assert.Single(Mail.Sent).To);   // sent before it was turned off
        Assert.Single(Mail.Attempted);                                   // nothing was tried for the second
        Assert.Equal(0, await QueueCountAsync());
        Assert.DoesNotContain(Logs.Entries.Skip(mark), e => e.Level >= LogLevel.Warning);
    }
}
