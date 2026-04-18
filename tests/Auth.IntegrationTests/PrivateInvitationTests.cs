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
        Assert.Equal(["email", "has_private_account", "org_name", "role"], mine.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.True(mine.GetProperty("has_private_account").GetBoolean());
        Assert.Equal("Acme", mine.GetProperty("org_name").GetString());
        Assert.False((await TenancyApi.ReadOkAsync(forNew)).GetProperty("has_private_account").GetBoolean());
    }

    [Fact]
    public async Task A_company_members_address_is_still_already_member()   // criterion 17
    {
        await AddMemberAsync(await DevCompanyIdAsync(), "member@poczta.test", "user");
        var (_, invite, token) = await InvitedAsync("member@poczta.test");

        using var preview = await TenancyApi.Preview(Client, token);
        using var accept = await TenancyApi.AcceptLeaving(Client, token);

        await TenancyApi.AssertErrorAsync(preview, HttpStatusCode.Conflict, "already_member");
        await TenancyApi.AssertErrorAsync(accept, HttpStatusCode.Conflict, "already_member");
        Assert.NotNull(await InviteAsync(invite));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task Accepting_without_leave_private_account_is_409_private_account_exists_and_changes_nothing(bool? leave)   // criterion 18
    {
        var (user, space) = await CreatePrivateAccountAsync(Private);
        var before = await SessionApi.LoginAsync(Client, Private, UserPassword);
        var (_, invite, token) = await InvitedAsync();
        var hash = (await InviteAsync(invite))!.TokenHash;
        object body = leave is null
            ? new { token, password = Ignored }
            : new { token, password = Ignored, leave_private_account = leave.Value };

        using var response = await TenancyApi.Send(Client, HttpMethod.Post, TenancyApi.AcceptPath, null, body);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "private_account_exists");
        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == user && m.CompanyId == space, TestContext.Current.CancellationToken)));
        Assert.Equal(hash, (await InviteAsync(invite))!.TokenHash);   // the invitation still works
        Assert.Empty(await AuditAsync(AuditKinds.InviteAccepted));
        _ = await SessionApi.RefreshOk(Client, before.RefreshToken);   // the session from before the refusal is not ended
        var session = await SessionApi.LoginAsync(Client, Private, UserPassword);
        Assert.Equal("personal", AccessTokens.Text(session.AccessToken, "org_kind"));
        using var moved = await TenancyApi.AcceptLeaving(Client, token);
        Assert.Equal(HttpStatusCode.NoContent, moved.StatusCode);
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
    public async Task Accepting_with_leave_private_account_moves_the_account_and_deletes_its_space()   // criterion 19
    {
        var (user, space) = await CreatePrivateAccountAsync(Private);
        var before = await SessionApi.LoginAsync(Client, Private, UserPassword);
        var (company, invite, token) = await InvitedAsync(role: "user");

        using (var accepted = await TenancyApi.AcceptLeaving(Client, token, Ignored))
        {
            await TenancyApi.AssertEmptyAsync(accepted, HttpStatusCode.NoContent);
        }

        Assert.Equal(0, await InDbAsync(db => db.Companies.CountAsync(c => c.Id == space, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await InDbAsync(db => db.CompanyRoles.CountAsync(r => r.CompanyId == space, TestContext.Current.CancellationToken)));
        var membership = Assert.Single(await InDbAsync(db => db.Memberships.AsNoTracking().Where(m => m.UserId == user).ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(company, membership.CompanyId);
        Assert.Equal(await RoleIdAsync(company, "user"), membership.RoleId);
        Assert.Null(await InviteAsync(invite));

        using (var refresh = await SessionApi.Refresh(Client, before.RefreshToken))
        {
            await SessionApi.AssertInvalidGrantAsync(refresh);   // every session ended
        }

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
    public async Task The_move_ends_the_mail_links_and_the_streak_of_the_account()   // criterion 19: as at any acceptance
    {
        var (user, _) = await CreatePrivateAccountAsync(Private);
        await EnqueueAsync(MailKind.PasswordReset, Private);
        await DispatchAsync();
        using (var wrong = await LoginApi.Login(Client, Private, WrongPassword))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        Assert.Equal(1, await InDbAsync(db => db.EmailTokens.CountAsync(t => t.UserId == user, TestContext.Current.CancellationToken)));   // what the move must end
        Assert.Equal(1, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));

        var (_, _, token) = await InvitedAsync();
        using (var accepted = await TenancyApi.AcceptLeaving(Client, token))
        {
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        }

        Assert.Equal(0, await InDbAsync(db => db.EmailTokens.CountAsync(t => t.UserId == user, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task An_account_with_a_company_membership_besides_the_personal_one_is_already_member()   // a state only a direct change of the database leaves
    {
        var (user, space) = await CreatePrivateAccountAsync(Private);
        var elsewhere = await DevCompanyIdAsync();
        var role = await RoleIdAsync(elsewhere, "user");
        await InDbAsync(async db =>
        {
            // Joined later than the personal space, so that the earliest membership is the personal one.
            db.Memberships.Add(new Membership { UserId = user, CompanyId = elsewhere, RoleId = role, JoinedAt = StorableTime.Now(Clock).AddHours(1) });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return 0;
        });
        var (_, invite, token) = await InvitedAsync();

        using var preview = await TenancyApi.Preview(Client, token);
        using var accept = await TenancyApi.AcceptLeaving(Client, token);

        await TenancyApi.AssertErrorAsync(preview, HttpStatusCode.Conflict, "already_member");
        await TenancyApi.AssertErrorAsync(accept, HttpStatusCode.Conflict, "already_member");
        Assert.NotNull(await InviteAsync(invite));
        Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(c => c.Id == space, TestContext.Current.CancellationToken)));
        Assert.Equal(2, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == user, TestContext.Current.CancellationToken)));
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
