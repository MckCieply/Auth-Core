using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010, criterion 3: the preview and the completion of a link whose kind is switched off, with the other kind on.</summary>
public sealed class PrivateSignupSwitchTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private static async Task<int> PendingCountAsync(AuthAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().PendingSignups.CountAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task With_only_organization_sign_up_on_a_private_link_is_403_and_stays()
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSignup();
        using var client = factory.CreateClient();
        var token = await PendingSignupSeed.IssueAsync(factory.Services, "ola@poczta.test", null, SignupKind.Private);

        using var preview = await SignupApi.Preview(client, token);
        using var complete = await SignupApi.CompletePrivate(client, token, "Chosen-Passw0rd");

        await SignupApi.AssertDisabledAsync(preview);
        await SignupApi.AssertDisabledAsync(complete);
        Assert.Equal(1, await PendingCountAsync(factory));
    }

    [Fact]
    public async Task With_only_private_sign_up_on_an_organization_link_is_403_and_stays()
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithPrivateSignup(organization: false);
        using var client = factory.CreateClient();
        var token = await PendingSignupSeed.IssueAsync(factory.Services, "anna@firma.test", "Firma");

        using var preview = await SignupApi.Preview(client, token);
        using var complete = await SignupApi.Complete(client, token, "Chosen-Passw0rd", "Firma");

        await SignupApi.AssertDisabledAsync(preview);
        await SignupApi.AssertDisabledAsync(complete);
        Assert.Equal(1, await PendingCountAsync(factory));
    }

    [Fact]
    public async Task With_only_private_sign_up_on_a_private_link_previews_and_completes()   // spec 0010 → preview step 3, Complete step 4: the link's own switch
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithPrivateSignup(organization: false);
        using var client = factory.CreateClient();
        var token = await PendingSignupSeed.IssueAsync(factory.Services, "ola@poczta.test", null, SignupKind.Private);

        using (var preview = await SignupApi.Preview(client, token))
        {
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.Equal("""{"kind":"private","email":"ola@poczta.test"}""", await preview.Content.ReadAsStringAsync());
        }

        using var complete = await SignupApi.CompletePrivate(client, token, "Chosen-Passw0rd");
        var session = await SessionApi.ReadSessionAsync(complete);

        Assert.Equal("personal", AccessTokens.Text(session.AccessToken, "org_kind"));
        Assert.Equal(0, await PendingCountAsync(factory));
    }

    [Fact]
    public async Task With_only_private_sign_up_on_a_body_that_is_not_an_object_is_400_and_not_403()   // spec 0010 → step 1 is "both off", not "organization off"
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithPrivateSignup(organization: false);
        using var client = factory.CreateClient();

        using var preview = await AccountApi.PostRaw(client, SignupApi.PreviewPath, "not json");
        using var complete = await AccountApi.PostRaw(client, SignupApi.CompletePath, "not json");

        await AccountApi.AssertErrorAsync(preview, AccountApi.InvalidRequest);
        await AccountApi.AssertErrorAsync(complete, AccountApi.InvalidRequest);
    }

    [Fact]
    public async Task With_both_switches_off_a_link_of_either_kind_is_403_before_the_body_and_stays()   // spec 0010 → preview step 1, Complete step 1
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSetting(MailSettingsLoader.CompleteSignupUrlKey, AuthAppFactory.DefaultCompleteSignupUrl);
        using var client = factory.CreateClient();
        var privateToken = await PendingSignupSeed.IssueAsync(factory.Services, "ola@poczta.test", null, SignupKind.Private);
        var organizationToken = await PendingSignupSeed.IssueAsync(factory.Services, "anna@firma.test", "Firma");

        using var previewPrivate = await SignupApi.Preview(client, privateToken);
        using var previewOrganization = await SignupApi.Preview(client, organizationToken);
        using var completePrivate = await SignupApi.CompletePrivate(client, privateToken, "Chosen-Passw0rd");
        using var completeOrganization = await SignupApi.Complete(client, organizationToken, "Chosen-Passw0rd", "Firma");
        using var previewBody = await AccountApi.PostRaw(client, SignupApi.PreviewPath, "not json");
        using var completeBody = await AccountApi.PostRaw(client, SignupApi.CompletePath, "not json");

        foreach (var response in new[] { previewPrivate, previewOrganization, completePrivate, completeOrganization, previewBody, completeBody })
        {
            await SignupApi.AssertDisabledAsync(response);
        }

        Assert.Equal(2, await PendingCountAsync(factory));
    }
}
