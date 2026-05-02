using System.Net;
using Auth.FakeOidc;
using Auth.Infrastructure.Identity;
using Auth.Server.Providers;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>The Google person of the fake (verified by default), or another address, subject or defect.</summary>
    protected static FakeSignIn GooglePerson(string? email = null, bool? verified = null, string? subject = null, string? defect = null) =>
        new(FakePeople.Google, email, verified, subject, Defect: defect);

    /// <summary>The personal Microsoft account of the fake (verified when it has an address).</summary>
    protected static FakeSignIn PersonalMicrosoft(string? email = null, string? subject = null, string? defect = null) =>
        new(FakePeople.MicrosoftPersonal, email, Subject: subject, Defect: defect);

    /// <summary>The Microsoft work account of the fake: unverified unless <paramref name="edov"/> says so.</summary>
    protected static FakeSignIn WorkMicrosoft(string? email = null, string? edov = null, string? subject = null, string? defect = null) =>
        new(FakePeople.MicrosoftWork, email, Subject: subject, Edov: edov, Defect: defect);

    /// <summary>The fake's answer to the start's redirect for this person: the path and query of the callback, with the code and the state.</summary>
    protected async Task<string> AuthorizeAsync(ProviderApi.Start start, FakeSignIn person)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(person);

        using var answer = await Fake.Client.GetAsync(person.AppendTo(start.Location.AbsoluteUri), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Found, answer.StatusCode);
        var back = answer.Headers.Location ?? throw new InvalidOperationException("The fake did not redirect back.");
        Assert.Equal(start.Query["redirect_uri"], back.GetLeftPart(UriPartial.Path));
        return back.PathAndQuery;
    }

    protected Task<HttpResponseMessage> CallbackAsync(string pathAndQuery, string? cookie) => ProviderApi.CallbackAsync(Browser, pathAndQuery, cookie);

    /// <summary>A private account made through a provider as the finish makes it (no password), and its session.</summary>
    protected async Task<(Guid UserId, SessionApi.Session Session)> PrivateThroughProviderAsync(string provider, FakeSignIn person)
    {
        var ticket = await TicketAsync(provider, person);
        using var finished = await ProviderApi.Finish(Client, ticket, "private");
        var session = await SessionApi.ReadSessionAsync(finished);
        return (Guid.Parse(AccessTokens.Text(session.AccessToken, "sub")), session);
    }

    /// <summary>The tickets in the table.</summary>
    protected Task<int> TicketCountAsync() => InDbAsync(db => db.ProviderTickets.CountAsync(TestContext.Current.CancellationToken));

    /// <summary>Makes the identity a login of the account straight in the database, as a link leaves it.</summary>
    protected Task LinkAsync(Guid userId, string provider, string key) =>
        InDbAsync(async db =>
        {
            ProviderLogins.Add(db, userId, provider, key);
            return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

    /// <summary>An account with no password (as a finish leaves it), a member of the development company.</summary>
    protected async Task<Guid> PasswordlessAccountAsync(string email)
    {
        var user = await CreateUserAsync(email, confirmed: true);
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var found = await users.FindByIdAsync(user.Id.ToString());
        Assert.True((await users.RemovePasswordAsync(found!)).Succeeded);
        return user.Id;
    }

    /// <summary>A whole round trip in one browser: the start, the provider, the callback; the ticket and its binding.</summary>
    protected async Task<ProviderApi.Ticket> TicketAsync(string provider, FakeSignIn person, string query = "")
    {
        var start = await StartAsync(provider, query);
        using var callback = await CallbackAsync(await AuthorizeAsync(start, person), start.StateCookie);
        return ProviderApi.TicketOf(callback);
    }
}
