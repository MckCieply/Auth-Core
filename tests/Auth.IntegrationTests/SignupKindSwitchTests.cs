using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → <c>POST /auth/signup</c>, checks 1 to 3: the switch of the kind comes before the other members.</summary>
public sealed class SignupKindSwitchTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private static async Task<int> PendingCountAsync(AuthAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().PendingSignups
            .CountAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A refusal comes before the limit, the queue and the audit log: none of them holds a row for it.</summary>
    private static async Task AssertNothingWasMadeAsync(AuthAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var cancellation = TestContext.Current.CancellationToken;
        Assert.Equal(0, await db.PendingSignups.CountAsync(cancellation));
        Assert.Equal(0, await db.MailRequests.CountAsync(cancellation));
        Assert.Equal(0, await db.MailRequestLimits.CountAsync(cancellation));
        Assert.Equal(0, await db.AuditEvents.CountAsync(a => a.Kind == AuditKinds.SignupRequested, cancellation));
    }

    [Theory]
    [InlineData("""{"kind":"private","email":5}""")]
    [InlineData("""{"kind":"private","email":"ola@poczta.test"}""")]
    [InlineData("""{"kind":"private","email":"ola@poczta.test","company_name":"Firma"}""")]
    public async Task With_only_organization_sign_up_on_a_private_request_is_403_before_its_other_members_are_judged(string body)   // criterion 3
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSignup();
        using var client = factory.CreateClient();

        using var response = await AccountApi.PostRaw(client, SignupApi.RequestPath, body);

        await SignupApi.AssertDisabledAsync(response);
        await AssertNothingWasMadeAsync(factory);
    }

    [Theory]
    [InlineData("""{"email":"bad","company_name":""}""")]
    [InlineData("""{"email":"anna@firma.test","company_name":"Firma"}""")]
    [InlineData("""{"kind":"organization","email":"anna@firma.test","company_name":"Firma"}""")]
    public async Task With_only_private_sign_up_on_an_organization_request_is_403_before_its_other_members_are_judged(string body)   // criterion 3
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithPrivateSignup(organization: false);
        using var client = factory.CreateClient();

        using var response = await AccountApi.PostRaw(client, SignupApi.RequestPath, body);

        await SignupApi.AssertDisabledAsync(response);
        await AssertNothingWasMadeAsync(factory);
    }

    [Fact]
    public async Task With_one_switch_on_a_kind_that_is_not_one_of_the_two_is_400_first()   // criterion 3: the kind is read before its switch is judged
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithPrivateSignup(organization: false);
        using var client = factory.CreateClient();

        using var response = await AccountApi.PostRaw(client, SignupApi.RequestPath, """{"kind":"company","email":"ola@poczta.test"}""");

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidRequest);
    }

    [Fact]
    public async Task With_only_private_sign_up_on_a_private_request_is_accepted()   // criterion 4
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithPrivateSignup(organization: false);
        using var client = factory.CreateClient();

        using var response = await AccountApi.PostRaw(client, SignupApi.RequestPath, """{"kind":"private","email":"ola@poczta.test"}""");

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        Assert.Equal(1, await PendingCountAsync(factory));
    }
}
