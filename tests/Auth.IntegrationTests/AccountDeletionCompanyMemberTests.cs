using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Api;
using Auth.Server.Audit;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0016 → <c>DELETE /auth/account</c> answers <c>403 company_member</c> for an account that holds a personal space and a company.</summary>
public sealed class AccountDeletionCompanyMemberTests : AuditTestBase
{
    private const string Private = "ola@poczta.test";
    private const string PrivateNormalized = "OLA@POCZTA.TEST";
    private const string Path = "/auth/account";

    public AccountDeletionCompanyMemberTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
    }

    private Task<HttpResponseMessage> DeleteAsync(string? token, string password = UserPassword) =>
        TenancyApi.Send(Client, HttpMethod.Delete, Path, token, new { password });

    private Task<int> UsersAsync(Guid user) => InDbAsync(db => db.Users.CountAsync(u => u.Id == user, TestContext.Current.CancellationToken));

    private Task<int> MembershipsAsync(Guid user) => InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == user, TestContext.Current.CancellationToken));

    private Task<int> StreaksAsync() => InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken));

    /// <summary>A private account with its personal space, joined to the development company as a plain member.</summary>
    private async Task<(Guid User, Guid Space, Guid Company)> BothKindsAsync(string role = "user")
    {
        var (user, space) = await CreatePrivateAccountAsync(Private);
        var company = await DevCompanyIdAsync();
        await AddMembershipAsync(user, company, role);
        return (user, space, company);
    }

    private async Task AssertNothingHappenedAsync(Guid user, int memberships)
    {
        Assert.Equal(1, await UsersAsync(user));
        Assert.Equal(memberships, await MembershipsAsync(user));
        Assert.Equal(0, await StreaksAsync());   // nothing was counted
        Assert.Empty(await AuditAsync(AuditKinds.AccountDeleted));
        Assert.Empty(await AuditAsync(AuditKinds.AccountDeleteRefused));   // a company_member writes no row
    }

    [Fact]
    public async Task A_personal_token_of_an_account_with_a_company_is_403_company_member()   // criterion 15
    {
        var (user, space, _) = await BothKindsAsync();
        var session = await SessionApi.LoginAsync(Client, Private, UserPassword);
        var personal = await SessionApi.SwitchOk(Client, session.RefreshToken, space);   // a sign-in picks by last use: make the token the personal one
        Assert.Equal("personal", AccessTokens.Text(personal.AccessToken, "org_kind"));

        using var response = await DeleteAsync(personal.AccessToken, "Wrong-Password-1");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "company_member");   // not wrong_password: no password is evaluated
        await AssertNothingHappenedAsync(user, 2);
    }

    [Fact]
    public async Task A_company_token_of_an_account_with_both_kinds_is_403_company_member()   // criterion 15
    {
        var (user, _, company) = await BothKindsAsync();
        var session = await SessionApi.LoginAsync(Client, Private, UserPassword);
        var inCompany = await SessionApi.SwitchOk(Client, session.RefreshToken, company);

        using var response = await DeleteAsync(inCompany.AccessToken, "Wrong-Password-1");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "company_member");
        await AssertNothingHappenedAsync(user, 2);
    }

    [Fact]
    public async Task The_answer_does_not_wait_for_the_body()
    {
        var (user, space, _) = await BothKindsAsync();
        var session = await SessionApi.LoginAsync(Client, Private, UserPassword);
        var personal = await SessionApi.SwitchOk(Client, session.RefreshToken, space);
        using var request = new HttpRequestMessage(HttpMethod.Delete, Path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", personal.AccessToken);
        request.Content = new StringContent("not json", Encoding.UTF8);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");

        using var response = await Client.SendAsync(request);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "company_member");
        await AssertNothingHappenedAsync(user, 2);
    }

    [Fact]
    public async Task After_the_company_is_deleted_by_the_account_the_deletion_succeeds()   // criterion 16
    {
        var (user, _, _) = await CreatePrivateWithOwnCompanyAsync();
        var first = await SessionApi.LoginAsync(Client, Private, UserPassword);
        var company = await InDbAsync(db => db.Companies.Where(c => c.Name == "Ola Ltd").Select(c => c.Id).SingleAsync(TestContext.Current.CancellationToken));
        var inCompany = await SessionApi.SwitchOk(Client, first.RefreshToken, company);
        using (var deleted = await SendFromCallerAsync(HttpMethod.Delete, OrgEndpoints.OrgPath, inCompany.AccessToken, new { name = "Ola Ltd", password = UserPassword }))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        var again = await SessionApi.LoginAsync(Client, Private, UserPassword);   // the personal space
        Assert.Equal("personal", AccessTokens.Text(again.AccessToken, "org_kind"));
        using var response = await DeleteAsync(again.AccessToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await UsersAsync(user));
    }

    private async Task<(Guid User, Guid Space, Guid Company)> CreatePrivateWithOwnCompanyAsync()
    {
        var (user, space) = await CreatePrivateAccountAsync(Private);
        var company = await CreateCompanyAsync("Ola Ltd");
        await AddMembershipAsync(user, company, "admin");
        return (user, space, company);
    }

    [Fact]
    public async Task An_account_with_companies_only_is_403_forbidden()   // criterion 17
    {
        var token = (await SessionApi.LoginAsync(Client, Factory)).AccessToken;

        using var response = await DeleteAsync(token, "Wrong-Password-1");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal(0, await StreaksAsync());
    }

    [Fact]
    public async Task A_personal_only_account_gets_the_answers_of_spec_0010()
    {
        var (user, _) = await CreatePrivateAccountAsync(Private);
        var session = await SessionApi.LoginAsync(Client, Private, UserPassword);

        using var wrong = await DeleteAsync(session.AccessToken, "Wrong-Password-1");
        await TenancyApi.AssertErrorAsync(wrong, HttpStatusCode.Forbidden, "wrong_password");
        Assert.Equal(1, await UsersAsync(user));

        using var right = await DeleteAsync(session.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, right.StatusCode);
        Assert.Equal(0, await UsersAsync(user));
    }

    [Fact]
    public async Task A_token_whose_company_is_no_longer_a_membership_is_403_permissions_changed()   // criterion 17
    {
        var (user, space, company) = await BothKindsAsync();
        var session = await SessionApi.LoginAsync(Client, Private, UserPassword);
        var personal = await SessionApi.SwitchOk(Client, session.RefreshToken, space);
        await RemoveMembershipAsync(user, space);   // the personal space is gone, the company remains

        using var spaceGone = await DeleteAsync(personal.AccessToken);
        await TenancyApi.AssertErrorAsync(spaceGone, HttpStatusCode.Forbidden, "permissions_changed");

        // The other way: a token of the company, the company membership is gone too (the personal space was removed above), so the token names a membership that does not exist.
        var inCompany = await SessionApi.SwitchOk(Client, session.RefreshToken, company);
        await RemoveMembershipAsync(user, company);
        using var companyGone = await DeleteAsync(inCompany.AccessToken);
        await TenancyApi.AssertErrorAsync(companyGone, HttpStatusCode.Forbidden, "permissions_changed");
        Assert.Equal(1, await UsersAsync(user));
    }

    [Fact]
    public async Task A_company_membership_added_while_the_request_waits_for_the_lock_is_403_company_member()
    {
        var (user, _) = await CreatePrivateAccountAsync(Private);
        var session = await SessionApi.LoginAsync(Client, Private, UserPassword);
        var company = await DevCompanyIdAsync();
        Task<HttpResponseMessage> pending;

        await using (var held = await HeldLock.AddressAsync(Factory, PrivateNormalized))
        {
            pending = DeleteAsync(session.AccessToken);
            await HeldLock.WaitForWaitersAsync(Factory, 1, pending);
            await AddMembershipAsync(user, company, "user");
            await held.ReleaseAsync();
        }

        using var response = await pending;
        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "company_member");
        await AssertNothingHappenedAsync(user, 2);
    }

    [Fact]
    public async Task A_personal_space_removed_while_the_request_waits_is_403_permissions_changed()
    {
        var (user, space) = await CreatePrivateAccountAsync(Private);
        var session = await SessionApi.LoginAsync(Client, Private, UserPassword);
        Task<HttpResponseMessage> pending;

        await using (var held = await HeldLock.AddressAsync(Factory, PrivateNormalized))
        {
            pending = DeleteAsync(session.AccessToken);
            await HeldLock.WaitForWaitersAsync(Factory, 1, pending);
            await RemoveMembershipAsync(user, space);
            await held.ReleaseAsync();
        }

        using var response = await pending;
        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "permissions_changed");
        Assert.Equal(1, await UsersAsync(user));
        Assert.Equal(0, await StreaksAsync());
        Assert.Empty(await AuditAsync(AuditKinds.AccountDeleted));
    }
}
