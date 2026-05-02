using System.Net;
using Auth.FakeOidc;
using Auth.Infrastructure.Identity;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Lockout;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → link, steps 5 and 6: an unknown address and a wrong password cost the same, and a locked attempt costs nothing (spec 0003).</summary>
public sealed class ProviderLinkTimingTests : ProviderTestBase
{
    private const string Ola = FakePeople.GoogleEmail;

    private readonly CountingPasswordHasher _hasher = new();

    public ProviderLinkTimingTests(PostgresFixture postgres, KeyMaterialFixture keys)
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
    public async Task An_address_without_an_account_runs_exactly_one_verification_against_the_decoy()
    {
        var ticket = await TicketAsync("google", GooglePerson(email: "nobody@gmail.test", subject: "400000000000000000004"));

        using var response = await ProviderApi.Link(Client, ticket, UserPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal([Decoy], _hasher.VerifiedHashes.ToArray());
    }

    [Fact]
    public async Task An_account_without_a_password_runs_exactly_one_verification_against_the_decoy()
    {
        _ = await PasswordlessAccountAsync(Ola);
        var ticket = await TicketAsync("google", GooglePerson());

        using var response = await ProviderApi.Link(Client, ticket, UserPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal([Decoy], _hasher.VerifiedHashes.ToArray());
    }

    [Fact]
    public async Task A_wrong_password_runs_exactly_one_verification_against_the_stored_hash()
    {
        _ = await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());

        using var response = await ProviderApi.Link(Client, ticket, LockoutApi.WrongPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal([await StoredHashAsync(Ola)], _hasher.VerifiedHashes.ToArray());
    }

    [Fact]
    public async Task A_locked_attempt_runs_no_verification_at_all()
    {
        _ = await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());
        for (var i = 0; i < 10; i++)
        {
            Clock.Advance(LockoutApi.HumanPace);
            using var failed = await ProviderApi.Link(Client, ticket, LockoutApi.WrongPassword);
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        var before = _hasher.VerifiedHashes.Count;

        using var refused = await ProviderApi.Link(Client, ticket, UserPassword);

        await LockoutApi.AssertLockedAsync(refused);
        Assert.Equal(10, before);
        Assert.Equal(before, _hasher.VerifiedHashes.Count);
    }
}
