using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → Invitations: inviting a private person is a warning, not a refusal.</summary>
public sealed class PrivateInvitationTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    private const string Private = "ola@poczta.test";
    private const string Ignored = "Ignored-Passw0rd";

    /// <summary>An invitation of a new company "Acme" to the address, with its mail sent: the company, the invitation and its token.</summary>
    private async Task<(Guid Company, Guid Invite, string Token)> InvitedAsync(string address = Private, string role = "user")
    {
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, address, role);
        await EnqueueInvitationAsync(invite, address);
        await DispatchAsync();
        return (company, invite, TokenIn(Mail.Sent.Last(m => m.To == address)));
    }

    [Fact]
    public async Task Inviting_an_address_with_a_private_account_answers_as_inviting_an_unknown_address()   // criterion 16
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        await CreatePrivateAccountAsync(Private);
        var role = await RoleIdAsync(company, "user");

        using var toPrivate = await TenancyApi.Send(Client, HttpMethod.Post, "/auth/org/invites", token, new { email = Private, role_id = role });
        using var toUnknown = await TenancyApi.Send(Client, HttpMethod.Post, "/auth/org/invites", token, new { email = "nobody@poczta.test", role_id = role });

        Assert.Equal(HttpStatusCode.Accepted, toPrivate.StatusCode);
        Assert.Equal(toUnknown.StatusCode, toPrivate.StatusCode);
        Assert.Equal(await toUnknown.Content.ReadAsStringAsync(), await toPrivate.Content.ReadAsStringAsync());
        Assert.Equal(LoginApi.HeaderNames(toUnknown), LoginApi.HeaderNames(toPrivate));
    }

    [Fact]
    public async Task The_preview_says_has_private_account_true_for_a_private_account_and_false_otherwise()   // criterion 17
    {
        await CreatePrivateAccountAsync(Private);
        var (_, _, privateToken) = await InvitedAsync(Private);
        var (_, _, newToken) = await InvitedAsync("nobody@poczta.test");

        using var forPrivate = await TenancyApi.Preview(Client, privateToken);
        using var forNew = await TenancyApi.Preview(Client, newToken);

        var mine = await TenancyApi.ReadOkAsync(forPrivate);
        Assert.Equal(["email", "has_account", "has_private_account", "org_id", "org_name", "role"], mine.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.True(mine.GetProperty("has_private_account").GetBoolean());
        Assert.True(mine.GetProperty("has_account").GetBoolean());
        Assert.Equal("Acme", mine.GetProperty("org_name").GetString());
        var fresh = await TenancyApi.ReadOkAsync(forNew);
        Assert.False(fresh.GetProperty("has_private_account").GetBoolean());
        Assert.False(fresh.GetProperty("has_account").GetBoolean());
    }

    [Fact]
    public async Task The_preview_says_has_private_account_true_also_for_an_account_with_a_company()   // spec 0017, criterion 2
    {
        var (user, _) = await CreatePrivateAccountAsync(Private);
        await AddMembershipAsync(user, await DevCompanyIdAsync(), "user");
        var (_, _, token) = await InvitedAsync(Private);

        using var preview = await TenancyApi.Preview(Client, token);

        var body = await TenancyApi.ReadOkAsync(preview);
        Assert.True(body.GetProperty("has_private_account").GetBoolean());
        Assert.True(body.GetProperty("has_account").GetBoolean());
    }

    [Fact]
    public async Task A_member_of_another_company_joins_and_a_leave_private_account_true_is_ignored()   // spec 0017, criteria 3 and 7
    {
        var member = await AddMemberAsync(await DevCompanyIdAsync(), "member@poczta.test", "user");
        var (company, invite, token) = await InvitedAsync("member@poczta.test");

        using (var preview = await TenancyApi.Preview(Client, token))
        {
            var body = await TenancyApi.ReadOkAsync(preview);   // the invitation is for Acme: a member of another company gets 200
            Assert.False(body.GetProperty("has_private_account").GetBoolean());
        }

        using (var accept = await TenancyApi.AcceptLeaving(Client, token))   // no personal space: the flag is ignored
        {
            await TenancyApi.AssertEmptyAsync(accept, HttpStatusCode.NoContent);
        }

        Assert.Null(await InviteAsync(invite));
        Assert.Equal(
            new[] { await DevCompanyIdAsync(), company }.Order(),
            (await InDbAsync(db => db.Memberships.AsNoTracking().Where(m => m.UserId == member).ToListAsync(TestContext.Current.CancellationToken))).Select(m => m.CompanyId).Order());
        var row = await SingleAsync(AuditKinds.InviteAccepted);
        Assert.Equal(["role"], AuditApi.DetailNames(row));   // no left_private_account
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task Accepting_without_leave_private_account_joins_and_keeps_the_personal_space(bool? leave)   // spec 0017, criteria 5 and 13
    {
        var (user, space) = await CreatePrivateAccountAsync(Private);
        var before = await SessionApi.LoginAsync(Client, Private, UserPassword);
        var (company, invite, token) = await InvitedAsync();
        var spaceRole = await InDbAsync(db => db.CompanyRoles.CountAsync(r => r.CompanyId == space, TestContext.Current.CancellationToken));
        object body = leave is null
            ? new { token, password = Ignored }
            : new { token, password = Ignored, leave_private_account = leave.Value };

        using (var response = await TenancyApi.Send(Client, HttpMethod.Post, TenancyApi.AcceptPath, null, body))
        {
            await TenancyApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);   // never private_account_exists
        }

        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == user && m.CompanyId == space, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(c => c.Id == space, TestContext.Current.CancellationToken)));
        Assert.Equal(spaceRole, await InDbAsync(db => db.CompanyRoles.CountAsync(r => r.CompanyId == space, TestContext.Current.CancellationToken)));
        Assert.True(spaceRole > 0);
        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == user && m.CompanyId == company, TestContext.Current.CancellationToken)));
        Assert.Null(await InviteAsync(invite));   // used up
        var refreshed = await SessionApi.RefreshOk(Client, before.RefreshToken);   // the personal session goes on
        Assert.Equal("personal", AccessTokens.Text(refreshed.AccessToken, "org_kind"));
        Assert.Equal(company.ToString(), (await SingleAsync(AuditKinds.InviteAccepted)).OrgId.ToString());
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[true]")]
    public async Task A_leave_private_account_that_is_not_a_boolean_is_400_invalid_request(string value)   // criterion 18
    {
        await CreatePrivateAccountAsync(Private);
        var (_, invite, token) = await InvitedAsync();

        using var response = await AccountApi.PostRaw(Client, TenancyApi.AcceptPath, $$"""{"token":"{{token}}","leave_private_account":{{value}}}""");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.NotNull(await InviteAsync(invite));
    }

    [Fact]
    public async Task Accepting_with_leave_private_account_moves_the_account_and_deletes_its_space()   // criterion 19; spec 0017, criterion 6
    {
        var (user, space) = await CreatePrivateAccountAsync(Private);
        var dev = await DevCompanyIdAsync();
        await AddMembershipAsync(user, dev, "user");
        var stamp = await InDbAsync(db => db.Users.AsNoTracking().Where(u => u.Id == user).Select(u => u.SecurityStamp).SingleAsync(TestContext.Current.CancellationToken));
        var first = await SessionApi.LoginAsync(Client, Private, UserPassword);
        var personal = await SessionApi.SwitchOk(Client, first.RefreshToken, space);   // a session in the personal space
        var second = await SessionApi.LoginAsync(Client, Private, UserPassword);
        var elsewhere = await SessionApi.SwitchOk(Client, second.RefreshToken, dev);   // a session in the development company
        Assert.Equal("personal", AccessTokens.Text(personal.AccessToken, "org_kind"));
        Clock.Advance(TimeSpan.FromMinutes(1));   // the move's LastUsedAt is then later than the switches', so the next login takes the company
        var (company, invite, token) = await InvitedAsync(role: "user");

        using (var accepted = await TenancyApi.AcceptLeaving(Client, token, Ignored))
        {
            await TenancyApi.AssertEmptyAsync(accepted, HttpStatusCode.NoContent);
        }

        Assert.Equal(0, await InDbAsync(db => db.Companies.CountAsync(c => c.Id == space, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await InDbAsync(db => db.CompanyRoles.CountAsync(r => r.CompanyId == space, TestContext.Current.CancellationToken)));
        var memberships = await InDbAsync(db => db.Memberships.AsNoTracking().Where(m => m.UserId == user).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new[] { dev, company }.Order(), memberships.Select(m => m.CompanyId).Order());   // the company membership it had stays
        Assert.Equal(await RoleIdAsync(company, "user"), memberships.Single(m => m.CompanyId == company).RoleId);
        Assert.Null(await InviteAsync(invite));
        Assert.Equal(stamp, await InDbAsync(db => db.Users.AsNoTracking().Where(u => u.Id == user).Select(u => u.SecurityStamp).SingleAsync(TestContext.Current.CancellationToken)));

        using (var refresh = await SessionApi.Refresh(Client, personal.RefreshToken))
        {
            await SessionApi.AssertInvalidGrantAsync(refresh);   // the session of the personal space is over
        }

        var stillThere = await SessionApi.RefreshOk(Client, elsewhere.RefreshToken);   // the session that went to the development company goes on
        Assert.Equal(dev.ToString(), AccessTokens.Text(stillThere.AccessToken, "org_id"));

        using (var sent = await LoginApi.Login(Client, Private, Ignored))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, sent.StatusCode);   // the password of the acceptance was not used
        }

        var after = await SessionApi.LoginAsync(Client, Private, UserPassword);   // the old password signs in, to the company
        Assert.Equal(user.ToString(), AccessTokens.Text(after.AccessToken, "sub"));
        Assert.Equal(company.ToString(), AccessTokens.Text(after.AccessToken, "org_id"));
        Assert.Equal("organization", AccessTokens.Text(after.AccessToken, "org_kind"));
        Assert.Equal(["user"], AccessTokens.Array(after.AccessToken, "roles"));

        var row = await SingleAsync(AuditKinds.InviteAccepted);
        Assert.Equal(user, row.SubjectUserId);
        Assert.Equal(company, row.OrgId);
        Assert.Equal("user", AuditApi.Text(row, "role"));
        Assert.Equal("true", AuditApi.Raw(row, "left_private_account"));
        Assert.Equal(space.ToString(), AuditApi.Text(row, "personal_org_id"));
        Assert.Equal(["left_private_account", "personal_org_id", "role"], AuditApi.DetailNames(row));
        await AssertNoSecretsAsync(Ignored, UserPassword, token);
    }

    [Fact]
    public async Task The_move_keeps_the_mail_links_and_the_streak_of_the_account()   // spec 0017, criterion 10
    {
        var (user, _) = await CreatePrivateAccountAsync(Private);
        await EnqueueAsync(MailKind.PasswordReset, Private);
        await DispatchAsync();
        using (var wrong = await LoginApi.Login(Client, Private, WrongPassword))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        Assert.Equal(1, await InDbAsync(db => db.EmailTokens.CountAsync(t => t.UserId == user, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));

        var (_, _, token) = await InvitedAsync();
        using (var accepted = await TenancyApi.AcceptLeaving(Client, token))
        {
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        }

        Assert.Equal(1, await InDbAsync(db => db.EmailTokens.CountAsync(t => t.UserId == user, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task An_account_with_a_company_and_a_personal_space_moves_and_keeps_the_company()   // spec 0017
    {
        var (user, space) = await CreatePrivateAccountAsync(Private);
        var elsewhere = await DevCompanyIdAsync();
        await AddMembershipAsync(user, elsewhere, "user");   // joined later than the personal space
        var (company, invite, token) = await InvitedAsync();

        using var preview = await TenancyApi.Preview(Client, token);
        using (var accept = await TenancyApi.AcceptLeaving(Client, token))
        {
            await TenancyApi.AssertEmptyAsync(accept, HttpStatusCode.NoContent);
        }

        var body = await TenancyApi.ReadOkAsync(preview);
        Assert.True(body.GetProperty("has_private_account").GetBoolean());
        Assert.Null(await InviteAsync(invite));
        Assert.Equal(0, await InDbAsync(db => db.Companies.CountAsync(c => c.Id == space, TestContext.Current.CancellationToken)));
        Assert.Equal(
            new[] { elsewhere, company }.Order(),
            (await InDbAsync(db => db.Memberships.AsNoTracking().Where(m => m.UserId == user).ToListAsync(TestContext.Current.CancellationToken))).Select(m => m.CompanyId).Order());
    }

    [Theory]
    [InlineData("\"   \"")]
    [InlineData("42")]
    [InlineData("null")]
    public async Task A_password_that_is_blank_or_not_a_string_is_400_invalid_request_even_when_the_account_moves(string value)   // spec 0010 → Invitations, step 1
    {
        var (user, space) = await CreatePrivateAccountAsync(Private);
        var (_, invite, token) = await InvitedAsync();

        using var response = await AccountApi.PostRaw(
            Client, TenancyApi.AcceptPath, $$"""{"token":"{{token}}","password":{{value}},"leave_private_account":true}""");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.NotNull(await InviteAsync(invite));
        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == user && m.CompanyId == space, TestContext.Current.CancellationToken)));
        Assert.Empty(await AuditAsync(AuditKinds.InviteAccepted));
    }

    [Fact]
    public async Task A_weak_password_sent_with_the_move_is_not_used_and_does_not_refuse_it()   // spec 0010 → Invitations, step 5: the password is not used
    {
        var (user, space) = await CreatePrivateAccountAsync(Private);
        var (company, _, token) = await InvitedAsync();

        using (var accepted = await TenancyApi.AcceptLeaving(Client, token, "abc"))
        {
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        }

        var membership = Assert.Single(await InDbAsync(db => db.Memberships.AsNoTracking().Where(m => m.UserId == user).ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(company, membership.CompanyId);
        Assert.Equal(0, await InDbAsync(db => db.Companies.CountAsync(c => c.Id == space, TestContext.Current.CancellationToken)));
        _ = await SessionApi.LoginAsync(Client, Private, UserPassword);
    }

    [Fact]
    public async Task Accepting_for_an_address_without_an_account_still_needs_a_password()   // criterion 20
    {
        var (_, invite, token) = await InvitedAsync("nobody@poczta.test");

        using (var missing = await AccountApi.PostRaw(Client, TenancyApi.AcceptPath, $$"""{"token":"{{token}}"}"""))
        {
            await TenancyApi.AssertErrorAsync(missing, HttpStatusCode.BadRequest, "invalid_request");
        }

        Assert.NotNull(await InviteAsync(invite));
        using var good = await TenancyApi.Accept(Client, token, "Chosen-Passw0rd");
        Assert.Equal(HttpStatusCode.NoContent, good.StatusCode);
    }

    [Fact]
    public async Task A_missing_password_with_an_unknown_token_is_invalid_token()   // spec 0010 → Invitations
    {
        using var response = await AccountApi.PostRaw(Client, TenancyApi.AcceptPath, $$"""{"token":"{{new string('x', 43)}}"}""");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_token");
    }

    [Fact]
    public void The_new_error_is_a_409()
    {
        Assert.Equal(409, TenancyErrors.StatusOf(TenancyErrors.PrivateAccountExists));
        Assert.Equal("private_account_exists", TenancyErrors.PrivateAccountExists);
    }
}
