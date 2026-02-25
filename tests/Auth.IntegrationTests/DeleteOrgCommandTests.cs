using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Admin;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

public sealed class DeleteOrgCommandTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    private const string Org = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void The_command_takes_an_org_and_the_name_to_confirm_in_any_order()
    {
        var expected = new DeleteOrgCommand(Org, "Acme sp. z o.o.");

        Assert.Equal(expected, AdminArguments.Parse(["delete-org", "--org", Org, "--confirm", "Acme sp. z o.o."]).Command);
        Assert.Equal(expected, AdminArguments.Parse(["delete-org", "--confirm=Acme sp. z o.o.", "--org=" + Org]).Command);
    }

    [Theory]
    [InlineData("delete-org")]
    [InlineData("delete-org --org " + Org)]
    [InlineData("delete-org --confirm Acme")]
    [InlineData("delete-org --org " + Org + " --confirm Acme --force")]
    [InlineData("delete-org --org " + Org + " --confirm Acme extra")]
    public void A_command_without_both_options_or_with_more_is_a_usage_error(string line)
    {
        var result = AdminArguments.Parse(line.Split(' '));

        Assert.Null(result.Command);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void The_usage_names_the_command()
    {
        Assert.Contains("delete-org    --org <id> --confirm <name>", AdminArguments.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_deletes_with_the_exact_name_prints_one_line_and_touches_nothing_else()   // criterion 11
    {
        var company = await CreateCompanyAsync("Acme sp. z o.o.");
        var other = await CreateCompanyAsync("Globex");

        var run = await OperatorCli.RunAsync(Factory, "delete-org", "--org", company.ToString(), "--confirm", "Acme sp. z o.o.");

        Assert.Equal(AdminCli.Done, run.Exit);
        Assert.Equal("Company deleted; every session of its members has ended.", run.Out.Trim());
        Assert.Equal("", run.Error);
        var left = await InDbAsync(db => db.Companies.AsNoTracking().Select(c => c.Id).ToListAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(company, left);
        Assert.Contains(other, left);
    }

    [Theory]
    [InlineData("acme")]          // not the same case
    [InlineData("Acme ")]         // a trailing space
    [InlineData(" Acme")]
    [InlineData("Acme Corp")]
    [InlineData("")]
    public async Task It_refuses_a_confirmation_that_is_not_exactly_the_name_and_deletes_nothing(string confirm)   // criterion 11
    {
        var company = await CreateCompanyAsync("Acme");

        var run = await OperatorCli.RunAsync(Factory, "delete-org", "--org", company.ToString(), "--confirm=" + confirm);

        Assert.Equal(AdminCli.Refused, run.Exit);
        Assert.Contains("error: invalid_request", run.Error, StringComparison.Ordinal);
        Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(c => c.Id == company, TestContext.Current.CancellationToken)));
        Assert.Empty(await AuditAsync(AuditKinds.OrgDeleted));
    }

    [Fact]
    public async Task It_refuses_a_company_that_does_not_exist_and_an_id_that_is_not_one()
    {
        var unknown = await OperatorCli.RunAsync(Factory, "delete-org", "--org", Org, "--confirm", "Acme");
        var malformed = await OperatorCli.RunAsync(Factory, "delete-org", "--org", "not-an-id", "--confirm", "Acme");

        Assert.Equal(AdminCli.Refused, unknown.Exit);
        Assert.Contains("error: not_found", unknown.Error, StringComparison.Ordinal);
        Assert.Equal(AdminCli.Refused, malformed.Exit);
        Assert.Contains("error: invalid_request", malformed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_command_that_is_not_understood_is_exit_code_2_and_deletes_nothing()
    {
        var company = await CreateCompanyAsync("Acme");

        var run = await OperatorCli.RunAsync(Factory, "delete-org", "--org", company.ToString());

        Assert.Equal(AdminCli.UsageError, run.Exit);
        Assert.Contains("usage: auth-server admin", run.Error, StringComparison.Ordinal);
        Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(c => c.Id == company, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task The_operator_is_not_bound_by_the_rule_that_nobody_acts_on_a_member_who_holds_more()   // criterion 11
    {
        var company = await CreateCompanyAsync("Acme");
        await AddMemberAsync(company, "boss@acme.test", "admin");   // holds *

        var run = await OperatorCli.RunAsync(Factory, "delete-org", "--org", company.ToString(), "--confirm", "Acme");

        Assert.Equal(AdminCli.Done, run.Exit);
    }
}
