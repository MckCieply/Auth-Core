using System.Net;
using Auth.Infrastructure.Identity;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class PasswordPolicyTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    [Theory]
    [InlineData("Abcdefg1", true)]                  // 8 characters, no special character needed
    [InlineData("Correct-Horse-Battery-1", true)]
    [InlineData("Abcdef1", false)]                  // 7 characters
    [InlineData("abcdefg1", false)]                 // no uppercase
    [InlineData("ABCDEFG1", false)]                 // no lowercase
    [InlineData("Abcdefgh", false)]                 // no digit
    [InlineData("zażółć12Ż", true)]                 // the only uppercase letter is not A-Z
    [InlineData("ZAŻÓŁĆ12ż", true)]                 // the only lowercase letter is not a-z
    [InlineData("zażółć123", false)]                // letters of any script, but none uppercase
    public async Task Policy_is_eight_characters_upper_lower_and_digit(string password, bool acceptable)   // criterion 8
    {
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var succeeded = true;
        foreach (var validator in users.PasswordValidators)
        {
            succeeded &= (await validator.ValidateAsync(users, new ApplicationUser(), password)).Succeeded;
        }

        Assert.Equal(acceptable, succeeded);
    }

    [Fact]
    public async Task Policy_does_not_block_a_login_with_a_password_set_before_it()
    {
        const string Weak = "abc";
        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByEmailAsync(Factory.SeedEmail))!;
            user.PasswordHash = users.PasswordHasher.HashPassword(user, Weak);
            Assert.True((await users.UpdateAsync(user)).Succeeded);
        }

        using var response = await LoginApi.Login(Client, Factory.SeedEmail, Weak);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
