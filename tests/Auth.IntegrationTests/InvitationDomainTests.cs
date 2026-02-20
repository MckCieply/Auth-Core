using System.Globalization;
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Admin;
using Auth.Server.Requests;
using Auth.Server.Tenancy;
using Microsoft.Extensions.Configuration;

namespace Auth.IntegrationTests;

public sealed class InvitationDomainTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    [Theory]
    [InlineData("x@example.pl")]
    [InlineData("x@żółw.pl")]
    [InlineData("x@пример.рф")]
    [InlineData("x@xn--e1afmkfd.xn--p1ai")]
    [InlineData("x@xn--w-uga1v8h.pl")]
    [InlineData("joe@mail.acme.co.uk")]
    [InlineData("joe@1and1.example")]
    [InlineData("JOE@ACME.TEST")]
    public void A_domain_that_ends_in_letters_or_in_xn_and_more_passes_the_domain_rule(string email)   // criterion 7
    {
        Assert.True(EmailInput.IsInvitable(email));
    }

    [Theory]
    [InlineData("x@127.0x1")]
    [InlineData("x@host.123")]
    [InlineData("x@0x7f.1")]
    [InlineData("x@0x7f.0x0.0x0.0x1")]
    [InlineData("x@10.0.0.5")]
    [InlineData("x@1.2.3.4a1")]
    [InlineData("x@example.c0m")]
    [InlineData("x@example.c")]
    [InlineData("x@example.xn--")]
    [InlineData("x@example.xn--@")]
    [InlineData("x@example.1")]
    public void A_domain_whose_last_label_is_not_letters_or_xn_and_more_is_refused(string email)   // criterion 7
    {
        Assert.False(EmailInput.IsInvitable(email));
    }

    private async Task<HttpResponseMessage> InviteAsync(string token, string email, Guid role) =>
        await TenancyApi.Send(Client, HttpMethod.Post, "/auth/org/invites", token, new { email, role_id = role });

    [Theory]
    [InlineData("x@127.0x1")]
    [InlineData("x@host.123")]
    [InlineData("x@0x7f.1")]
    public async Task The_api_answers_400_invalid_request_for_such_a_domain(string email)   // criterion 7
    {
        var (company, _, token) = await CompanyWithAdminAsync();

        using var response = await InviteAsync(token, email, await RoleIdAsync(company, "user"));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Empty(await InDbAsync(db => Task.FromResult(db.Invites.ToList())));
    }

    [Theory]
    [InlineData("x@example.pl")]
    [InlineData("x@żółw.pl")]   // an address typed in punycode is not here: IsMailbox (unchanged) does not take it, MimeKit decodes its Address
    public async Task The_api_takes_the_domains_that_pass(string email)   // criterion 7
    {
        var (company, _, token) = await CompanyWithAdminAsync();

        using var response = await InviteAsync(token, email, await RoleIdAsync(company, "user"));

        await TenancyApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
    }

    private async Task<(int Exit, string Error)> InviteByCliAsync(Guid company, string email)
    {
        _ = Factory.Services;
        var error = new StringWriter(CultureInfo.InvariantCulture);
        var exit = await AdminCli.RunAsync(
            ["invite", "--org", company.ToString(), "--email", email, "--role", "user"],
            new StringWriter(CultureInfo.InvariantCulture),
            error,
            builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Auth"] = Factory.ConnectionString,
                [ManifestSettings.PathKey] = Factory.ManifestPath,
            }),
            TestContext.Current.CancellationToken);
        return (exit, error.ToString());
    }

    [Theory]
    [InlineData("x@127.0x1")]
    [InlineData("x@host.123")]
    [InlineData("x@0x7f.1")]
    public async Task The_cli_refuses_such_a_domain_too(string email)   // criterion 7
    {
        var company = await CreateCompanyAsync();

        var (exit, error) = await InviteByCliAsync(company, email);

        Assert.Equal(AdminCli.Refused, exit);
        Assert.Contains("error: invalid_request", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_cli_takes_a_domain_that_passes()   // criterion 7
    {
        var company = await CreateCompanyAsync();

        var (exit, error) = await InviteByCliAsync(company, "x@example.pl");

        Assert.True(exit == AdminCli.Done, error);
    }
}
