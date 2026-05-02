using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Providers;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → Configuration, the test issuer (criterion 34): both providers as plain OpenID Connect clients of the fake, same code.</summary>
public sealed class ProviderTestIssuerTests : ProviderTestBase
{
    public ProviderTestIssuerTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithSetting(ProviderSettings.TestIssuerKey, "http://fake-oidc.test");
    }

    [Theory]
    [InlineData("google", FakePeople.Google, FakePeople.GoogleSubject)]
    [InlineData("microsoft", FakePeople.MicrosoftPersonal, FakePeople.PersonalTenant + ":" + FakePeople.PersonalObjectId)]
    public async Task Both_providers_run_through_the_plain_registration_of_the_test_issuer(string provider, string person, string key)
    {
        var start = await StartAsync(provider);
        Assert.Equal($"http://fake-oidc.test/{provider}/authorize", start.Location.GetLeftPart(UriPartial.Path));

        using var callback = await CallbackAsync(await AuthorizeAsync(start, new FakeSignIn(person)), start.StateCookie);

        Assert.Equal("sign_in", ProviderApi.TicketOf(callback).Purpose);
        Assert.Equal(0, Fake.Server.UserInfoRequests);   // userinfo is off for the plain registration too
        var row = await InDbAsync(db => db.ProviderTickets.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(provider, row.Provider);
        Assert.Equal(key, row.IdentityKey);
        Assert.True(row.EmailVerified);
    }

    [Fact]
    public async Task Under_the_test_issuer_openiddict_checks_the_issuer_itself()   // Amendment A6
    {
        var start = await StartAsync("microsoft");

        using var callback = await CallbackAsync(
            await AuthorizeAsync(start, new FakeSignIn(FakePeople.MicrosoftPersonal, Defect: FakeDefects.WrongIssuer)), start.StateCookie);

        Assert.Equal("provider_error", ProviderApi.FragmentOf(callback)["error"]);
        Assert.Equal(0, await InDbAsync(db => db.ProviderTickets.CountAsync(TestContext.Current.CancellationToken)));
    }
}
