using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Tenancy;
using Microsoft.Extensions.Configuration;

namespace Auth.IntegrationTests;

public sealed class CompanyLimitSettingsTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private static CompanyLimitSettings Load(string? value) =>
        CompanyLimitSettings.Load(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [CompanyLimitSettings.Key] = value })
            .Build());

    private static string RepositoryFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Auth-Core.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, relative);
    }

    [Fact]
    public void Absent_blank_and_white_space_mean_20()
    {
        Assert.Equal(20, CompanyLimitSettings.Load(new ConfigurationBuilder().Build()).MaxCompaniesPerAccount);
        Assert.Equal(20, Load("").MaxCompaniesPerAccount);
        Assert.Equal(20, Load("   ").MaxCompaniesPerAccount);
        Assert.Equal(20, CompanyLimitSettings.Default);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("1000", 1000)]
    [InlineData(" 7 ", 7)]
    [InlineData("0020", 20)]
    public void A_whole_number_from_1_to_1000_is_taken(string text, int expected) =>
        Assert.Equal(expected, Load(text).MaxCompaniesPerAccount);

    [Theory]
    [InlineData("0")]
    [InlineData("1001")]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData("20.5")]
    [InlineData("abc")]
    [InlineData("1e2")]
    [InlineData("123456789012")]
    public void Anything_else_stops_the_load_and_names_the_key(string text)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Load(text));

        Assert.Contains("Auth:Tenancy:MaxCompaniesPerAccount", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_ASCII_digit_stops_the_load_and_names_the_key()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Load(((char)0x0663).ToString()));

        Assert.Contains("Auth:Tenancy:MaxCompaniesPerAccount", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_service_stops_at_start_for_a_bad_value()
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSetting(CompanyLimitSettings.Key, "0");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(CompanyLimitSettings.Key, ex.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("deploy/docker-compose.yml")]
    [InlineData("deploy/docker-compose.prod.yml")]
    [InlineData("deploy/.env.prod.example")]
    [InlineData(".env.example")]
    [InlineData("docs/deployment/vps.md")]
    public void The_shipped_files_name_the_variable(string file) =>
        Assert.Contains("AUTH_MAX_COMPANIES_PER_ACCOUNT", File.ReadAllText(RepositoryFile(file)), StringComparison.Ordinal);

    [Theory]
    [InlineData("deploy/docker-compose.yml")]
    [InlineData("deploy/docker-compose.prod.yml")]
    public void The_compose_files_pass_the_variable_to_the_key(string file) =>
        Assert.Contains("Auth__Tenancy__MaxCompaniesPerAccount: ${AUTH_MAX_COMPANIES_PER_ACCOUNT:-}", File.ReadAllText(RepositoryFile(file)), StringComparison.Ordinal);
}
