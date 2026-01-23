using System.Net;
using Auth.Infrastructure.Identity;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Lockout;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Auth.IntegrationTests;

public sealed class LoginTimingTests : SessionTestBase
{
    private readonly CountingPasswordHasher _hasher = new();

    public LoginTimingTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IPasswordHasher<ApplicationUser>>(_hasher)));
    }

    private string Decoy => Factory.Services.GetRequiredService<DecoyPasswordHash>().Value;

    private async Task<string> StoredHashAsync(string email)
    {
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return (await users.FindByEmailAsync(email))!.PasswordHash!;
    }

    [Fact]
    public async Task Unknown_email_runs_exactly_one_verification_against_the_decoy()   // criterion 6
    {
        using var response = await LoginApi.Login(Client, "nobody@example.com", LockoutApi.WrongPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal([Decoy], _hasher.VerifiedHashes.ToArray());
    }

    [Fact]
    public async Task Wrong_password_runs_exactly_one_verification_against_the_stored_hash()   // criterion 6
    {
        using var response = await LoginApi.Login(Client, Factory.SeedEmail, LockoutApi.WrongPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal([await StoredHashAsync(Factory.SeedEmail)], _hasher.VerifiedHashes.ToArray());
    }

    [Fact]
    public async Task Account_without_a_password_runs_the_decoy_too()
    {
        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var created = await users.CreateAsync(new ApplicationUser { UserName = "invited@example.com", Email = "invited@example.com" });
            Assert.True(created.Succeeded);
        }

        using var response = await LoginApi.Login(Client, "invited@example.com", LockoutApi.WrongPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("""{"error":"invalid_credentials"}""", await response.Content.ReadAsStringAsync());
        Assert.Equal([Decoy], _hasher.VerifiedHashes.ToArray());
    }

    [Fact]
    public async Task Locked_attempt_runs_no_verification_at_all()   // Decision 7
    {
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 10);
        var before = _hasher.VerifiedHashes.Count;

        using var refused = await LoginApi.Login(Client, Factory.SeedEmail, Factory.SeedPassword);

        await LockoutApi.AssertLockedAsync(refused);
        Assert.Equal(10, before);
        Assert.Equal(before, _hasher.VerifiedHashes.Count);
    }

    [Fact]
    public async Task Decoy_has_the_cost_parameters_of_a_real_hash()   // Decision 11
    {
        var stored = Convert.FromBase64String(await StoredHashAsync(Factory.SeedEmail));
        var decoy = Convert.FromBase64String(Decoy);

        // Identity's hash starts with a 13-byte header: format marker, PRF, iteration count, salt size.
        Assert.Equal(stored[..13], decoy[..13]);
        Assert.Equal(stored.Length, decoy.Length);
    }
}
