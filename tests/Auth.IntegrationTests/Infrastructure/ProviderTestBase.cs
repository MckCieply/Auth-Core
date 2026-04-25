using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// Base of the provider tests (spec 0011): the sign-up host of <see cref="SignupTestBase"/> with both kinds of sign-up on, both providers listed
/// and configured, and the fake provider in process behind OpenIddict's HTTP client, on the test's clock. <see cref="Browser"/> plays the browser
/// at Auth-Core (it never follows a redirect and sends no cookie by itself); <c>Fake.Client</c> plays it at the provider.
/// </summary>
public abstract class ProviderTestBase : SignupTestBase
{
    private HttpClient? _browser;

    protected ProviderTestBase(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Fake = new FakeOidcHost(Clock);
        Factory.WithPrivateSignup()
            .WithProviders()
            .WithServices(services => services.AddSingleton(Fake.Routing));
    }

    protected FakeOidcHost Fake { get; }

    protected HttpClient Browser =>
        _browser ??= Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    public override async ValueTask DisposeAsync()
    {
        _browser?.Dispose();
        await base.DisposeAsync();
        await Fake.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Makes this manifest active in the running host at once (the switches and the offered providers follow it), as a start with another file
    /// would; both providers stay listed unless <paramref name="withProviders"/> is <see langword="false"/>.
    /// </summary>
    protected void SetManifest(string yaml, bool withProviders = true)
    {
        var parsed = ManifestParser.Parse(yaml + (withProviders ? AuthAppFactory.ProvidersLine : ""));
        Assert.Null(parsed.Error);
        Holder.Set(parsed.Manifest!, null);
    }

    protected Task<ProviderApi.Start> StartAsync(string provider, string query = "") => ProviderApi.StartAsync(Browser, provider, query);
}
