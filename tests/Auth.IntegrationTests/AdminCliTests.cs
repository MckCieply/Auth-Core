using System.Globalization;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Admin;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Auth.IntegrationTests;

public sealed class AdminCliTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private const string Boss = "boss@acme.test";

    private sealed record Run(int Exit, string Out, string Error);

    /// <summary>Runs the command the way the container does: the same binary, the configuration of the service.</summary>
    private Task<Run> RunAsync(params string[] args) => RunAgainstAsync(Factory.ConnectionString, Factory.ManifestPath, args);

    private static async Task<Run> RunAgainstAsync(string? connectionString, string manifestPath, string[] args, ILoggerProvider? logs = null)
    {
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var error = new StringWriter(CultureInfo.InvariantCulture);
        var exit = await AdminCli.RunAsync(
            args,
            output,
            error,
            builder =>
            {
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Auth"] = connectionString,
                    [ManifestSettings.PathKey] = manifestPath,
                });
                if (logs is not null)
                {
                    builder.Logging.AddProvider(logs);
                }
            },
            TestContext.Current.CancellationToken);
        return new Run(exit, output.ToString(), error.ToString());
    }

    /// <summary>Starts the host, which migrates and seeds the database, and then runs the command against it.</summary>
    private async Task<Run> CliAsync(params string[] args)
    {
        _ = Factory.Services;
        return await RunAsync(args);
    }

    private async Task<Guid> CreateOrgAsync(string name)
    {
        var run = await CliAsync("create-org", "--name", name);
        Assert.True(run.Exit == 0, run.Error);
        return Guid.Parse(run.Out.Trim());
    }

    private Task<List<Invite>> InvitesAsync() =>
        InDbAsync(db => db.Invites.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));

    // ---- create-org and list-orgs

    [Fact]
    public async Task Create_org_prints_the_id_and_makes_a_company_with_a_copy_of_the_default_roles()   // criterion 1
    {
        var run = await CliAsync("create-org", "--name", "Acme");

        Assert.Equal(0, run.Exit);
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\n$", run.Out.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal("", run.Error);   // nothing but the id on the output, and no log lines on the error stream
        var id = Guid.Parse(run.Out.Trim());
        var roles = await InDbAsync(db => db.CompanyRoles.AsNoTracking().Where(r => r.CompanyId == id).OrderBy(r => r.Name).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal([("admin", "*"), ("user", "reports:read,reports:approve")], roles.Select(r => (r.Name, string.Join(',', r.Permissions))));
    }

    [Fact]
    public async Task List_orgs_shows_the_companies_with_their_members_sorted_by_name()   // criterion 1
    {
        var acme = await CreateOrgAsync("Acme");
        await AddMemberAsync(acme, Boss, "admin");
        await AddMemberAsync(acme, "worker@acme.test", "user");

        var run = await RunAsync("list-orgs");

        Assert.Equal(0, run.Exit);
        var lines = run.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(
            [$"{acme}\tAcme\t2", $"{await DevCompanyIdAsync()}\tDevelopment\t1"],   // ordinal: A before D
            lines);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" Acme")]
    [InlineData("Acme ")]
    public async Task Create_org_refuses_a_name_that_breaks_the_rules(string name)
    {
        var before = await InDbAsync(db => db.Companies.CountAsync(TestContext.Current.CancellationToken));

        var run = await CliAsync("create-org", "--name=" + name);

        Assert.Equal(1, run.Exit);
        Assert.Contains("error: invalid_request", run.Error);
        Assert.Equal("", run.Out);
        Assert.Equal(before, await InDbAsync(db => db.Companies.CountAsync(TestContext.Current.CancellationToken)));
    }

    // ---- invite

    [Fact]
    public async Task Invite_queues_a_mail_for_the_first_admin_and_the_dispatcher_sends_it()   // criterion 2
    {
        var acme = await CreateOrgAsync("Acme");

        var run = await RunAsync("invite", "--org", acme.ToString(), "--email", Boss, "--role", "admin");

        Assert.Equal(0, run.Exit);
        Assert.Contains("within a minute", run.Out);
        var invite = Assert.Single(await InvitesAsync());
        Assert.Null(invite.InvitedBy);   // the operator
        Assert.Equal(await RoleIdAsync(acme, "admin"), invite.RoleId);
        Clock.Advance(TimeSpan.FromMinutes(5));   // the command stamped real time; the test host runs on its own clock
        await DispatchAsync();
        var mail = Assert.Single(Mail.Sent);
        Assert.Equal(Boss, mail.To);
        Assert.Contains("Acme", mail.TextBody);
        Assert.Contains("as admin", mail.TextBody);
        Assert.Contains(AuthAppFactory.DefaultInviteUrl + "?token=", mail.TextBody);
    }

    [Fact]
    public async Task The_role_is_found_by_its_name_without_regard_to_case()
    {
        var acme = await CreateOrgAsync("Acme");

        var run = await RunAsync("invite", "--org", acme.ToString(), "--email", Boss, "--role", "ADMIN");

        Assert.Equal(0, run.Exit);
        Assert.Equal(await RoleIdAsync(acme, "admin"), Assert.Single(await InvitesAsync()).RoleId);
    }

    [Fact]
    public async Task Invite_refuses_what_the_company_api_refuses_with_the_same_codes()
    {
        var acme = await CreateOrgAsync("Acme");
        await AddMemberAsync(acme, "worker@acme.test", "user");
        var ok = await RunAsync("invite", "--org", acme.ToString(), "--email", Boss, "--role", "admin");
        Assert.Equal(0, ok.Exit);

        var cases = new (string Code, string[] Args)[]
        {
            ("already_in_org", ["invite", "--org", acme.ToString(), "--email", "worker@acme.test", "--role", "user"]),
            ("invite_pending", ["invite", "--org", acme.ToString(), "--email", "BOSS@acme.test", "--role", "user"]),
            ("not_found", ["invite", "--org", acme.ToString(), "--email", "new@acme.test", "--role", "nobody"]),
            ("not_found", ["invite", "--org", Guid.NewGuid().ToString(), "--email", "new@acme.test", "--role", "user"]),
            ("invalid_request", ["invite", "--org", "not-a-uuid", "--email", "new@acme.test", "--role", "user"]),
            ("invalid_request", ["invite", "--org", acme.ToString(), "--email", "not an address", "--role", "user"]),
            ("invalid_request", ["invite", "--org", acme.ToString(), "--email", "new@acme.test", "--role", ""]),
        };
        foreach (var (code, args) in cases)
        {
            var run = await RunAsync(args);

            Assert.Equal(1, run.Exit);
            Assert.Equal($"error: {code}", run.Error.Trim());
            Assert.Equal("", run.Out);
        }

        Assert.Single(await InvitesAsync());
    }

    [Fact]
    public async Task Invitations_from_the_command_line_go_through_the_mail_limit()   // criterion 10
    {
        var acme = await CreateOrgAsync("Acme");
        Assert.Equal(0, (await RunAsync("invite", "--org", acme.ToString(), "--email", Boss, "--role", "admin")).Exit);
        await InDbAsync(db => db.Invites.ExecuteDeleteAsync(TestContext.Current.CancellationToken));   // as if it had been cancelled

        var run = await RunAsync("invite", "--org", acme.ToString(), "--email", Boss, "--role", "admin");

        Assert.Equal(1, run.Exit);
        Assert.Matches(@"^error: too_many_attempts \(retry in \d+ seconds\)$", run.Error.Trim());
        Assert.Empty(await InvitesAsync());
    }

    // ---- remove-member

    [Fact]
    public async Task Remove_member_removes_the_member_and_ends_every_session()   // criterion 22
    {
        var acme = await CreateOrgAsync("Acme");
        await AddMemberAsync(acme, Boss, "admin");
        var worker = await AddMemberAsync(acme, "worker@acme.test", "user");
        var session = await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword);

        var run = await RunAsync("remove-member", "--org", acme.ToString(), "--email", "WORKER@acme.test");

        Assert.Equal(0, run.Exit);
        Assert.Null(await InDbAsync(db => db.Memberships.SingleOrDefaultAsync(m => m.UserId == worker, TestContext.Current.CancellationToken)));
        using var refresh = await SessionApi.Refresh(Client, session.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(refresh);
        using var login = await LoginApi.Login(Client, "worker@acme.test", UserPassword);
        Assert.Equal("""{"error":"no_membership"}""", await login.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Removing_the_last_manager_is_refused_unless_forced()   // criteria 16, 22
    {
        var acme = await CreateOrgAsync("Acme");
        var boss = await AddMemberAsync(acme, Boss, "admin");

        var refused = await RunAsync("remove-member", "--org", acme.ToString(), "--email", Boss);
        Assert.Equal(1, refused.Exit);
        Assert.Equal("error: last_manager", refused.Error.Trim());
        Assert.NotNull(await InDbAsync(db => db.Memberships.SingleOrDefaultAsync(m => m.UserId == boss, TestContext.Current.CancellationToken)));

        var forced = await RunAsync("remove-member", "--org", acme.ToString(), "--email", Boss, "--force");
        Assert.Equal(0, forced.Exit);
        Assert.Null(await InDbAsync(db => db.Memberships.SingleOrDefaultAsync(m => m.UserId == boss, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Remove_member_refuses_someone_who_is_not_a_member_of_that_company()
    {
        var acme = await CreateOrgAsync("Acme");
        var globex = await CreateOrgAsync("Globex");
        await AddMemberAsync(globex, "carol@globex.test", "user");

        foreach (var email in new[] { "carol@globex.test", "nobody@nowhere.test" })
        {
            var run = await RunAsync("remove-member", "--org", acme.ToString(), "--email", email);

            Assert.Equal(1, run.Exit);
            Assert.Equal("error: not_found", run.Error.Trim());
        }

        Assert.NotNull(await InDbAsync(db => db.Memberships.SingleOrDefaultAsync(m => m.CompanyId == globex, TestContext.Current.CancellationToken)));
    }

    // ---- arguments, configuration, the database

    [Theory]
    [InlineData("")]
    [InlineData("delete-org")]
    [InlineData("create-org")]
    [InlineData("create-org --name Acme --force")]
    [InlineData("list-orgs extra")]
    public async Task A_command_that_is_not_understood_is_a_usage_error_and_touches_nothing(string line)
    {
        var args = line.Length == 0 ? [] : line.Split(' ');

        var run = await CliAsync(args);

        Assert.Equal(2, run.Exit);
        Assert.StartsWith("error: ", run.Error);
        Assert.Contains("usage: auth-server admin", run.Error);
        Assert.Equal("", run.Out);
        Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(TestContext.Current.CancellationToken)));   // only the development company
    }

    [Fact]
    public async Task The_command_does_not_migrate_the_database()
    {
        var database = "auth_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(Postgres.ConnectionStringFor("postgres")).ConnectionString;
        await using (var admin = new NpgsqlConnection(connection))
        {
            await admin.OpenAsync(TestContext.Current.CancellationToken);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var run = await RunAgainstAsync(Postgres.ConnectionStringFor(database), Factory.ManifestPath, ["create-org", "--name", "Acme"]);

        Assert.Equal(3, run.Exit);
        Assert.StartsWith("error: failed (", run.Error);
        Assert.Equal("", run.Out);
        await using var check = new NpgsqlConnection(Postgres.ConnectionStringFor(database));
        await check.OpenAsync(TestContext.Current.CancellationToken);
        await using var tables = new NpgsqlCommand("SELECT to_regclass('\"Companies\"') IS NULL", check);
        Assert.True((bool)(await tables.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);   // still nothing in it
        NpgsqlConnection.ClearPool(check);
    }

    [Fact]
    public async Task A_missing_connection_string_is_a_failure_that_names_nothing_secret()
    {
        var run = await RunAgainstAsync(null, Factory.ManifestPath, ["list-orgs"]);

        Assert.Equal(3, run.Exit);
        Assert.Equal("error: failed (InvalidOperationException): Configuration value 'ConnectionStrings:Auth' is missing or blank.", run.Error.Trim());
    }

    [Fact]
    public async Task A_connection_string_never_reaches_the_output()
    {
        var run = await RunAgainstAsync("Host=127.0.0.1;Port=1;Database=x;Username=u;Password=SECRET-MARKER;Timeout=2", Factory.ManifestPath, ["list-orgs"]);

        Assert.Equal(3, run.Exit);
        Assert.DoesNotContain("SECRET-MARKER", run.Out + run.Error);
        Assert.DoesNotContain("127.0.0.1", run.Out + run.Error);
    }

    [Fact]
    public async Task A_broken_manifest_does_not_stop_the_command_and_the_stored_one_is_used()   // criterion 20
    {
        _ = Factory.Services;   // the host stores the valid manifest at its start
        File.WriteAllText(Factory.ManifestPath, "default_roles:\n  user: [reports:write]\n");

        var logs = new CapturingLoggerProvider();

        var run = await RunAgainstAsync(Factory.ConnectionString, Factory.ManifestPath, ["create-org", "--name", "Acme"], logs);

        Assert.Equal(0, run.Exit);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("was not used") && e.Message.Contains("reports:write"));   // the reason is logged
        var id = Guid.Parse(run.Out.Trim());
        var roles = await InDbAsync(db => db.CompanyRoles.AsNoTracking().Where(r => r.CompanyId == id).OrderBy(r => r.Name).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["admin", "user"], roles.Select(r => r.Name));   // the last valid manifest's defaults
    }
}
