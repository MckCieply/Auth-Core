using System.Net;
using System.Text.Json;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0016 → <c>POST /auth/orgs</c>: the refusals, in the order the contract gives them, and that a refusal writes nothing.</summary>
public sealed class OrgCreateRefusalTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    private const string ValidBody = """{"name":"Beta"}""";

    private void Turn(bool on) =>
        Holder.Set(ManifestParser.Parse(on ? AuthAppFactory.OrgCreationManifest : AuthAppFactory.DefaultManifest).Manifest!, null);

    private async Task<(int Companies, int Rows)> CountsAsync() =>
        (await InDbAsync(db => db.Companies.CountAsync(TestContext.Current.CancellationToken)),
         await InDbAsync(db => db.AuditEvents.CountAsync(TestContext.Current.CancellationToken)));

    [Fact]
    public async Task Without_a_token_the_answer_is_401_also_when_creation_is_off()   // Review Focus 1
    {
        Turn(false);

        using var off = await TenancyApi.CreateOrg(Client, null, new { name = "Beta" });
        await TenancyApi.AssertUnauthorizedAsync(off);

        Turn(true);
        using var on = await TenancyApi.CreateOrg(Client, null, new { name = "Beta" });
        await TenancyApi.AssertUnauthorizedAsync(on);
        using var garbage = await TenancyApi.CreateOrg(Client, "not-a-token", new { name = "Beta" });
        await TenancyApi.AssertUnauthorizedAsync(garbage);
    }

    [Fact]
    public async Task With_the_switch_off_the_answer_is_403_org_creation_disabled_also_for_a_body_that_is_not_json()   // criterion 5
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        Turn(false);

        using var valid = await TenancyApi.CreateOrg(Client, token, ValidBody);
        using var text = await TenancyApi.CreateOrg(Client, token, ValidBody, "text/plain");
        using var notJson = await TenancyApi.CreateOrg(Client, token, "not json");
        using var empty = await TenancyApi.CreateOrg(Client, token, "");
        using var none = await TenancyApi.CreateOrg(Client, token, null);

        foreach (var response in new[] { valid, text, notJson, empty, none })
        {
            await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "org_creation_disabled");
        }
    }

    [Fact]
    public async Task A_name_that_breaks_the_rule_is_400()   // criterion 6
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        Turn(true);
        var before = await CountsAsync();

        static string Named(string name) => JsonSerializer.Serialize(new { name });
        var bodies = new (string Body, string ContentType)[]
        {
            ("{}", "application/json"),
            ("""{"name":null}""", "application/json"),
            ("""{"name":5}""", "application/json"),
            (Named(""), "application/json"),
            (Named(new string('a', 101)), "application/json"),
            (Named(" Beta"), "application/json"),
            (Named("Beta "), "application/json"),
            (Named("Be" + (char)0x07 + "ta"), "application/json"),
            (Named("Be" + (char)0x200B + "ta"), "application/json"),
            (Named("Be" + (char)0xFDD0 + "ta"), "application/json"),
            ("[]", "application/json"),
            ("""["Beta"]""", "application/json"),
            ("not json", "application/json"),
            ("", "application/json"),
            (ValidBody, "text/plain"),
        };

        foreach (var (body, contentType) in bodies)
        {
            using var response = await TenancyApi.CreateOrg(Client, token, body, contentType);
            await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        }

        Assert.Equal(before, await CountsAsync());

        // Members that are not asked for are ignored.
        using var extra = await TenancyApi.CreateOrg(Client, token, """{"name":"Beta","role":"owner","extra":[1]}""");
        _ = await TenancyApi.ReadCreatedAsync(extra);
    }

    [Fact]
    public async Task Another_charset_is_415()   // criterion 6
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        Turn(true);
        var before = await CountsAsync();

        using var response = await TenancyApi.CreateOrg(Client, token, ValidBody, "application/json; charset=utf-16");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        Assert.Equal(before, await CountsAsync());
    }

    [Fact]
    public async Task A_token_of_a_deleted_account_with_a_valid_body_is_403_permissions_changed()   // criterion 7
    {
        var (_, admin, token) = await CompanyWithAdminAsync();
        Turn(true);

        await RemoveMembershipAsync(admin);
        using var withoutMembership = await TenancyApi.CreateOrg(Client, token, new { name = "Beta" });
        await TenancyApi.AssertErrorAsync(withoutMembership, HttpStatusCode.Forbidden, "permissions_changed");

        await InDbAsync(db => db.Users.Where(u => u.Id == admin).ExecuteDeleteAsync(TestContext.Current.CancellationToken));
        using var gone = await TenancyApi.CreateOrg(Client, token, new { name = "Beta" });
        await TenancyApi.AssertErrorAsync(gone, HttpStatusCode.Forbidden, "permissions_changed");
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Other_methods_are_405_with_Allow_naming_both(string method)
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        Turn(true);

        using var response = await TenancyApi.Send(Client, new HttpMethod(method), "/auth/orgs", token, new { name = "Beta" });

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        var allow = response.Content.Headers.Allow;
        Assert.Contains("GET", allow);
        Assert.Contains("POST", allow);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_creation_writes_no_row()   // criterion 6, 12
    {
        var (_, admin, token) = await CompanyWithAdminAsync();
        var before = await CountsAsync();

        Turn(false);
        using var off = await TenancyApi.CreateOrg(Client, token, new { name = "Beta" });
        Turn(true);
        using var bad = await TenancyApi.CreateOrg(Client, token, new { name = " " });
        using var charset = await TenancyApi.CreateOrg(Client, token, ValidBody, "application/json; charset=utf-16");
        using var anonymous = await TenancyApi.CreateOrg(Client, null, new { name = "Beta" });
        await RemoveMembershipAsync(admin);
        using var gone = await TenancyApi.CreateOrg(Client, token, new { name = "Beta" });

        Assert.Equal(
            [HttpStatusCode.Forbidden, HttpStatusCode.BadRequest, HttpStatusCode.UnsupportedMediaType, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden],
            [off.StatusCode, bad.StatusCode, charset.StatusCode, anonymous.StatusCode, gone.StatusCode]);
        Assert.Equal(before, await CountsAsync());
    }
}
