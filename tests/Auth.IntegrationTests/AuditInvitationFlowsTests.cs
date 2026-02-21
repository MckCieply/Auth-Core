using System.Net;
using System.Text.Json;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

public sealed class AuditInvitationFlowsTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    private const string Worker = "Worker@Acme.test";
    private const string NewPassword = "Brand-New-Passw0rd";
    private static readonly TimeSpan PastTheGap = TimeSpan.FromSeconds(61);

    private Task<HttpResponseMessage> InviteAsync(string token, string email, Guid role) =>
        TenancyApi.Send(Client, HttpMethod.Post, "/auth/org/invites", token, new { email, role_id = role });

    private async Task<HttpResponseMessage> AcceptFromCallerAsync(string link, string password)
    {
        using var request = RateLimitApi.Request(
            HttpMethod.Post, TenancyApi.AcceptPath, CallerAddress, null, JsonSerializer.Serialize(new { token = link, password }));
        return await Client.SendAsync(request);
    }

    private async Task<Guid> InviteIdAsync(string email) =>
        await InDbAsync(db => db.Invites.Where(i => i.Email == email).Select(i => i.Id).SingleAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task An_invitation_sent_is_recorded_with_the_address_as_typed_the_role_and_the_invitation()   // criterion 8
    {
        var (company, admin, token) = await CompanyWithAdminAsync();

        using var response = await SendFromCallerAsync(
            HttpMethod.Post, "/auth/org/invites", token, new { email = Worker, role_id = await RoleIdAsync(company, "user") });
        await TenancyApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);

        var row = await SingleAsync(AuditKinds.InviteSent);
        Assert.Equal(admin, row.ActorUserId);
        Assert.Null(row.SubjectUserId);   // the address may have no account, and is not looked up
        Assert.Equal(Worker, row.SubjectEmail);   // as typed
        Assert.Equal(company, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Equal(await InviteIdAsync(Worker), row.TargetId);
        Assert.Equal("user", AuditApi.Text(row, "role"));
        Assert.Equal(CallerAddress, row.ClientIp);
        Assert.Equal(["role"], AuditApi.DetailNames(row));
    }

    [Fact]
    public async Task An_invitation_that_is_refused_leaves_no_row()   // criterion 8: a failed change leaves no row
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var user = await RoleIdAsync(company, "user");
        using (var first = await InviteAsync(token, Worker, user))
        {
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        }

        using var pending = await InviteAsync(token, Worker, user);                          // 409 invite_pending
        using var badDomain = await InviteAsync(token, "x@127.0x1", user);                   // 400 invalid_request
        using var unknownRole = await InviteAsync(token, "other@acme.test", Guid.NewGuid()); // 404 not_found
        using var member = await InviteAsync(token, "boss@acme.test", user);                 // 409 already_in_org

        Assert.Equal(HttpStatusCode.Conflict, pending.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badDomain.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownRole.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, member.StatusCode);
        Assert.Single(await AuditAsync(AuditKinds.InviteSent));
    }

    [Fact]
    public async Task An_invitation_that_the_mail_limit_refuses_leaves_no_row_and_one_that_is_resent_is_its_own_kind()   // criterion 8
    {
        var (company, admin, token) = await CompanyWithAdminAsync();
        using (var sent = await InviteAsync(token, Worker, await RoleIdAsync(company, "user")))
        {
            Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        }

        var invite = await InviteIdAsync(Worker);
        using (var tooSoon = await TenancyApi.Send(Client, HttpMethod.Post, $"/auth/org/invites/{invite}/resend", token))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, tooSoon.StatusCode);   // one mail a minute
        }

        Assert.Empty(await AuditAsync(AuditKinds.InviteResent));

        Clock.Advance(PastTheGap);
        using var resent = await SendFromCallerAsync(HttpMethod.Post, $"/auth/org/invites/{invite}/resend", token);
        await TenancyApi.AssertEmptyAsync(resent, HttpStatusCode.Accepted);

        var row = await SingleAsync(AuditKinds.InviteResent);
        Assert.Equal(admin, row.ActorUserId);
        Assert.Equal(Worker, row.SubjectEmail);
        Assert.Equal(invite, row.TargetId);
        Assert.Equal("user", AuditApi.Text(row, "role"));
        Assert.Null(row.SubjectUserId);
        Assert.Equal(company, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Equal(CallerAddress, row.ClientIp);
        Assert.Equal(["role"], AuditApi.DetailNames(row));
        Assert.Single(await AuditAsync(AuditKinds.InviteSent));
    }

    [Fact]
    public async Task A_cancelled_invitation_is_recorded_and_one_that_is_not_found_is_not()   // criterion 8
    {
        var (company, admin, token) = await CompanyWithAdminAsync();
        using (var sent = await InviteAsync(token, Worker, await RoleIdAsync(company, "user")))
        {
            Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        }

        var invite = await InviteIdAsync(Worker);
        using (var missing = await TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/invites/{Guid.NewGuid()}", token))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        Assert.Empty(await AuditAsync(AuditKinds.InviteCancelled));

        using var cancelled = await SendFromCallerAsync(HttpMethod.Delete, $"/auth/org/invites/{invite}", token);
        await TenancyApi.AssertEmptyAsync(cancelled, HttpStatusCode.NoContent);

        var row = await SingleAsync(AuditKinds.InviteCancelled);
        Assert.Equal(admin, row.ActorUserId);
        Assert.Equal(Worker, row.SubjectEmail);
        Assert.Equal(invite, row.TargetId);
        Assert.Equal(company, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Null(row.SubjectUserId);
        Assert.Equal("user", AuditApi.Text(row, "role"));
        Assert.Equal(CallerAddress, row.ClientIp);
        Assert.Equal(["role"], AuditApi.DetailNames(row));
    }

    [Fact]
    public async Task An_invitation_made_by_the_operator_says_so_and_has_no_actor()   // criterion 8
    {
        var company = await CreateCompanyAsync("Acme");

        var run = await OperatorCli.RunAsync(Factory, "invite", "--org", company.ToString(), "--email", Worker, "--role", "user");
        Assert.Equal(0, run.Exit);

        var row = await SingleAsync(AuditKinds.InviteSent);
        Assert.Null(row.ActorUserId);
        Assert.Equal("cli", AuditApi.Text(row, "via"));
        Assert.Null(row.ClientIp);
        Assert.Equal("user", AuditApi.Text(row, "role"));
        Assert.Equal(Worker, row.SubjectEmail);
        Assert.Equal(company, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Equal(["role", "via"], AuditApi.DetailNames(row));
    }

    [Fact]
    public async Task An_accepted_invitation_is_recorded_without_the_link_or_the_password_and_a_refused_one_is_not()   // criteria 8
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        using (var sent = await InviteAsync(token, Worker, await RoleIdAsync(company, "user")))
        {
            Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        }

        await DispatchAsync();
        var link = TokenIn(Mail.Sent[^1]);
        var invite = await InviteIdAsync(Worker);

        using (var weak = await TenancyApi.Accept(Client, link, "abc"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        }

        using (var unknown = await TenancyApi.Accept(Client, "z".PadRight(43, 'z'), NewPassword))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        }

        Assert.Empty(await AuditAsync(AuditKinds.InviteAccepted));

        using var accepted = await AcceptFromCallerAsync(link, NewPassword);
        await TenancyApi.AssertEmptyAsync(accepted, HttpStatusCode.NoContent);

        var row = await SingleAsync(AuditKinds.InviteAccepted);
        var joined = await InDbAsync(db => db.Users.Where(u => u.Email == Worker).Select(u => u.Id).SingleAsync(TestContext.Current.CancellationToken));
        Assert.Null(row.ActorUserId);   // the person who holds the link is anonymous
        Assert.Equal(joined, row.SubjectUserId);
        Assert.Equal(Worker, row.SubjectEmail);
        Assert.Equal(company, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Equal(invite, row.TargetId);
        Assert.Equal("user", AuditApi.Text(row, "role"));
        Assert.Equal(CallerAddress, row.ClientIp);
        Assert.Equal(["role"], AuditApi.DetailNames(row));
        await AssertNoSecretsAsync(link, NewPassword, "abc");
    }
}
