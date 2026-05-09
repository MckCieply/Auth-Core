using Auth.Infrastructure.Identity;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Account;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>Spec 0004 → Password policy: the rules a password breaks, named as the contract names them, for any string the endpoints let through.</summary>
public sealed class PasswordRulesTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private async Task<IReadOnlyList<string>> BrokenAsync(string password)
    {
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return await PasswordRules.BrokenAsync(users, new ApplicationUser(), password);
    }

    [Fact]
    public async Task The_empty_password_breaks_the_four_rules_of_the_contract_and_nothing_else()
    {
        Assert.Equal(["too_short", "requires_upper", "requires_lower", "requires_digit"], await BrokenAsync(""));
    }

    [Theory]
    [InlineData("   ", new[] { "too_short", "requires_upper", "requires_lower", "requires_digit" })]
    [InlineData("short", new[] { "too_short", "requires_upper", "requires_digit" })]
    [InlineData("alllowercase1", new[] { "requires_upper" })]
    [InlineData("Fresh-Passw0rd-2", new string[0])]
    public async Task A_password_breaks_exactly_the_rules_it_breaks(string password, string[] rules)
    {
        Assert.Equal(rules, await BrokenAsync(password));
    }
}
