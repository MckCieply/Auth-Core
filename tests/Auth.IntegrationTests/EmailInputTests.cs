using System.Net;
using System.Text;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Requests;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class EmailInputTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    [Theory]
    [InlineData("user@example.com", true)]
    [InlineData("zażółć@example.com", true)]
    [InlineData("a\uD83D\uDE00b@example.com", true)]     // a well-formed surrogate pair
    [InlineData("a\uFFFEb@example.com", false)]          // the one that made the ICU normaliser throw (E1)
    [InlineData("a\uFFFFb@example.com", false)]
    [InlineData("a\uFDD0b@example.com", false)]
    [InlineData("a\uD83F\uDFFEb@example.com", false)]    // U+1FFFE, a noncharacter outside the BMP
    [InlineData("a\u0007b@example.com", false)]
    [InlineData("a\u007Fb@example.com", false)]          // DEL
    [InlineData("a\u0085b@example.com", false)]          // a C1 control character
    [InlineData("a@example.com\n", false)]
    public void Well_formedness_does_not_depend_on_the_host(string email, bool expected) =>
        Assert.Equal(expected, EmailInput.IsWellFormed(email));

    [Fact]
    public void Unpaired_surrogate_is_not_well_formed()
    {
        // Built at run time: an attribute argument cannot hold an unpaired surrogate.
        var email = "a" + (char)0xD800 + "b@example.com";

        Assert.False(EmailInput.IsWellFormed(email));
    }

    [Fact]
    public void Normalised_form_is_the_one_the_account_lookup_uses()
    {
        using var scope = Factory.Services.CreateScope();
        var normalizer = scope.ServiceProvider.GetRequiredService<ILookupNormalizer>();

        Assert.True(EmailInput.TryNormalize("User@Example.com", normalizer, out var normalized));
        Assert.Equal(normalizer.NormalizeEmail("User@Example.com"), normalized);
        Assert.False(EmailInput.TryNormalize("a\uFFFEb@example.com", normalizer, out _));
    }

    [Fact]
    public async Task Login_does_not_apply_the_length_limit_of_the_mail_endpoints()
    {
        using var response = await LoginApi.Login(Client, new string('a', 300) + "@example.com", "x");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_with_an_email_that_cannot_be_normalised_is_a_400_and_not_an_attempt()   // criterion 16
    {
        const string Body = """{"email":"a\uFFFEb@example.com","password":"x"}""";

        // Twelve: a counted identifier would be locked long before the last one.
        for (var i = 0; i < 12; i++)
        {
            using var response = await Client.PostAsync(LoginApi.Path, new StringContent(Body, Encoding.UTF8, "application/json"));
            var raw = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"Request {i + 1}: expected 400, got {(int)response.StatusCode}: {raw}");
            Assert.Contains("invalid_request", raw);
        }

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Assert.Equal(0, await db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken));
    }
}
