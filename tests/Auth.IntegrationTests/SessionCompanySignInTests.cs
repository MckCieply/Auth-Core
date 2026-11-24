using System.Net;
using Auth.FakeOidc;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.Server;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0015 → Session company: every sign-in records the space it signed in to in the refresh token (never the access token) and writes
/// <c>LastUsedAt</c> of that membership in the transaction of the session; a sign-in that is refused writes nothing.
/// </summary>
public sealed class SessionCompanySignInTests : ProviderTestBase
{
    private const string Ola = FakePeople.GoogleEmail;
    private const string Invited = "jan@firma.test";
    private const string JanSubject = "700000000000000000007";

    private static readonly string[] TenancyClaims = ["org_id", "org_kind", "roles", "permissions"];

    // While true, a handler of the test makes OpenIddict refuse every sign-in after the membership lock was taken: the answer is not a 200.
    private bool _refusing;

    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The whole Unix microseconds of an instant, worked out here and not by the code under test.</summary>
    private static long MicrosOf(DateTimeOffset instant) => (instant.UtcDateTime - UnixEpoch).Ticks / 10;

    public SessionCompanySignInTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithServices(services => services.Configure<OpenIddictServerOptions>(options => options.Handlers.Add(
            OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ProcessSignInContext>()
                .UseInlineHandler(context =>
                {
                    if (_refusing)
                    {
                        context.Reject(OpenIddictConstants.Errors.InvalidGrant, "Refused by the test.");
                    }

                    return ValueTask.CompletedTask;
                })
                .SetOrder(OpenIddictServerHandlers.EvaluateGeneratedTokens.Descriptor.Order + 600)   // after the handlers that decide the tokens, before any is made
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build())));
    }

    private Task<DateTimeOffset> JoinedAtAsync(Guid userId, Guid companyId) =>
        InDbAsync(db => db.Memberships.AsNoTracking()
            .Where(m => m.UserId == userId && m.CompanyId == companyId)
            .Select(m => m.JoinedAt)
            .SingleAsync(TestContext.Current.CancellationToken));

    /// <summary>Sets when a membership was last used, as a switch or an earlier sign-in would have left it.</summary>
    private Task<int> SetLastUsedAtAsync(Guid userId, Guid companyId, DateTimeOffset? at) =>
        InDbAsync(db => db.Memberships.Where(m => m.UserId == userId && m.CompanyId == companyId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastUsedAt, at), TestContext.Current.CancellationToken));

    private DateTimeOffset Now => StorableTime.Now(Clock);

    private static Guid SubjectOf(SessionApi.Session session) => Guid.Parse(AccessTokens.Text(session.AccessToken, "sub"));

    [Fact]
    public async Task A_login_stores_the_session_company_in_the_refresh_token_only()
    {
        var user = await Factory.SeedUserIdAsync();
        var company = await DevCompanyIdAsync();

        var session = await SessionApi.LoginAsync(Client, Factory);

        var stored = await StoredTokens.RefreshClaimsAsync(Factory, session.RefreshToken);
        Assert.Equal(company.ToString(), stored["session_org_id"]!.GetValue<string>());
        Assert.Equal(MicrosOf(await JoinedAtAsync(user, company)).ToString(System.Globalization.CultureInfo.InvariantCulture), stored["session_org_joined"]!.GetValue<string>());
        Assert.All(TenancyClaims, type => Assert.False(stored.ContainsKey(type), $"The refresh token carries '{type}'."));

        var access = AccessTokens.Payload(session.AccessToken);
        Assert.False(access.ContainsKey("session_org_id"));
        Assert.False(access.ContainsKey("session_org_joined"));
        Assert.Equal(AccessTokens.ContractClaims, access.Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    /// <summary>One sign-in path, arranged: the account it signs in, the space it takes, and (where the path allows one) another space it must leave alone.</summary>
    private sealed record Taken(Guid User, Guid Space, Guid? Other);

    private async Task<Guid> SecondCompanyForAsync(Guid user)
    {
        var other = await CreateCompanyAsync("Second");
        await AddMembershipAsync(user, other, "user");   // joined an hour after the first, so the sign-in does not choose it
        return other;
    }

    private async Task<Taken> SignInThroughAsync(string path)
    {
        switch (path)
        {
            case "login":
            {
                var user = await Factory.SeedUserIdAsync();
                var other = await SecondCompanyForAsync(user);
                var session = await SessionApi.LoginAsync(Client, Factory);
                Assert.Equal(user, SubjectOf(session));
                return new Taken(user, await DevCompanyIdAsync(), other);
            }

            case "provider sign-in":
            {
                var account = await CreateUserAsync(Ola, confirmed: true);
                await LinkAsync(account.Id, "google", FakePeople.GoogleSubject);
                var other = await SecondCompanyForAsync(account.Id);
                var ticket = await TicketAsync("google", GooglePerson());
                using var response = await ProviderApi.Exchange(Client, ticket);
                Assert.Equal(account.Id, SubjectOf(await SessionApi.ReadSessionAsync(response)));
                return new Taken(account.Id, await DevCompanyIdAsync(), other);
            }

            case "provider link":
            {
                var account = await CreateUserAsync(Ola, confirmed: true);
                var other = await SecondCompanyForAsync(account.Id);
                var ticket = await TicketAsync("google", GooglePerson());
                using var response = await ProviderApi.Link(Client, ticket, UserPassword);
                Assert.Equal(account.Id, SubjectOf(await SessionApi.ReadSessionAsync(response)));
                return new Taken(account.Id, await DevCompanyIdAsync(), other);
            }

            case "sign-up completion":
            {
                var token = await MailedAsync();
                using var done = await SignupApi.Complete(Client, token, Password, CompanyName);
                var user = SubjectOf(await SessionApi.ReadSessionAsync(done));
                return new Taken(user, await InDbAsync(db => db.Companies.Where(c => c.Name == CompanyName).Select(c => c.Id).SingleAsync(TestContext.Current.CancellationToken)), null);
            }

            case "provider finish":
            {
                var ticket = await TicketAsync("google", GooglePerson(email: "new@gmail.test", subject: "500000000000000000005"));
                using var finished = await ProviderApi.Finish(Client, ticket, "private");
                var user = SubjectOf(await SessionApi.ReadSessionAsync(finished));
                var space = await InDbAsync(db => db.Memberships.Where(m => m.UserId == user).Select(m => m.CompanyId).SingleAsync(TestContext.Current.CancellationToken));
                return new Taken(user, space, null);
            }

            case "invitation through a provider":
            {
                var company = await CreateCompanyAsync("Acme");
                var invite = await AddInviteAsync(company, Invited, "user");
                await EnqueueInvitationAsync(invite, Invited);
                await DispatchAsync();
                var token = TokenIn(Mail.Sent.Last(m => m.To == Invited));
                var ticket = await TicketAsync("google", GooglePerson(email: Invited, subject: JanSubject), "?invite=" + token);
                using var response = await ProviderApi.Exchange(Client, ticket);
                var user = SubjectOf(await SessionApi.ReadSessionAsync(response));
                return new Taken(user, company, null);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(path), path, "Unknown sign-in path.");
        }
    }

    [Theory]
    [InlineData("login")]
    [InlineData("provider sign-in")]
    [InlineData("provider link")]
    [InlineData("sign-up completion")]
    [InlineData("provider finish")]
    [InlineData("invitation through a provider")]
    public async Task Every_sign_in_path_writes_last_used_at_of_the_space_it_took(string path)
    {
        var taken = await SignInThroughAsync(path);

        Assert.Equal(Now, await LastUsedAtAsync(taken.User, taken.Space));
        if (taken.Other is { } other)
        {
            Assert.Null(await LastUsedAtAsync(taken.User, other));
        }
    }

    [Fact]
    public async Task A_refused_sign_in_leaves_last_used_at_unchanged()
    {
        var user = await Factory.SeedUserIdAsync();
        var company = await DevCompanyIdAsync();
        var unverified = await CreateUserAsync("unverified@acme.test", confirmed: false);
        var unverifiedSpace = await InDbAsync(db => db.Memberships.Where(m => m.UserId == unverified.Id).Select(m => m.CompanyId).SingleAsync(TestContext.Current.CancellationToken));

        using var wrong = await LoginApi.Login(Client, Factory.SeedEmail, "Not-The-Passw0rd");
        using var unconfirmed = await LoginApi.Login(Client, "unverified@acme.test", UserPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unconfirmed.StatusCode);
        Assert.Null(await LastUsedAtAsync(user, company));
        Assert.Null(await LastUsedAtAsync(unverified.Id, unverifiedSpace));
    }

    [Fact]
    public async Task A_sign_in_that_OpenIddict_refuses_after_the_membership_lock_leaves_last_used_at_unchanged()
    {
        var user = await Factory.SeedUserIdAsync();
        var company = await DevCompanyIdAsync();
        _refusing = true;

        using var refused = await LoginApi.Login(Client, Factory.SeedEmail, Factory.SeedPassword);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);   // the lock was taken and LastUsedAt written, then the sign-in was refused
        Assert.False(refused.Headers.Contains("Set-Cookie"));
        Assert.Null(await LastUsedAtAsync(user, company));
    }

    [Fact]
    public async Task A_login_after_a_switch_elsewhere_lands_in_the_company_used_last()   // criterion 4, first half
    {
        var user = await Factory.SeedUserIdAsync();
        var first = await DevCompanyIdAsync();
        var second = await SecondCompanyForAsync(user);
        await SetLastUsedAtAsync(user, first, Now.AddHours(-2));
        await SetLastUsedAtAsync(user, second, Now.AddHours(-1));

        var session = await SessionApi.LoginAsync(Client, Factory);

        Assert.Equal(second.ToString(), AccessTokens.Text(session.AccessToken, "org_id"));
    }

    [Fact]
    public async Task Removed_from_the_used_company_a_login_takes_the_next_space()   // criterion 4, second half
    {
        var user = await Factory.SeedUserIdAsync();
        var first = await DevCompanyIdAsync();
        var second = await SecondCompanyForAsync(user);
        await SetLastUsedAtAsync(user, second, Now.AddHours(-1));
        await RemoveMembershipAsync(user, second);

        var session = await SessionApi.LoginAsync(Client, Factory);

        Assert.Equal(first.ToString(), AccessTokens.Text(session.AccessToken, "org_id"));
    }

    [Fact]
    public async Task Two_companies_without_use_land_in_the_earlier_joined_and_a_company_beats_the_personal_space()   // criterion 3
    {
        var user = await Factory.SeedUserIdAsync();
        var dev = await DevCompanyIdAsync();
        var devJoined = await JoinedAtAsync(user, dev);
        var earlier = await CreateCompanyAsync("Earlier");
        await AddMembershipAsync(user, earlier, "user", devJoined.AddDays(-2));
        await InDbAsync(async db =>
        {
            PersonalSpaces.Add(db, user, devJoined.AddDays(-30));   // joined before both companies
            return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        var session = await SessionApi.LoginAsync(Client, Factory);

        Assert.Equal(earlier.ToString(), AccessTokens.Text(session.AccessToken, "org_id"));   // earlier joined; the personal space is older but not a company
        Assert.Equal("organization", AccessTokens.Text(session.AccessToken, "org_kind"));
    }
}
