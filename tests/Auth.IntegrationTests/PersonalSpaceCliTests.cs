using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Admin;
using Auth.Server.Seeding;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → The operator CLI: personal spaces are hidden.</summary>
public sealed class PersonalSpaceCliTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private const string Private = "ola@poczta.test";

    [Fact]
    public async Task List_orgs_does_not_list_personal_spaces()   // criterion 14
    {
        var (_, space) = await CreatePrivateAccountAsync(Private);

        var run = await OperatorCli.RunAsync(Factory, "list-orgs");

        Assert.Equal(AdminCli.Done, run.Exit);
        Assert.DoesNotContain(space.ToString(), run.Out, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\tprivate\t", run.Out, StringComparison.Ordinal);
        Assert.Contains("\t" + DevUserSeeder.DefaultOrgName + "\t", run.Out, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("invite")]
    [InlineData("remove-member")]
    [InlineData("delete-org")]
    public async Task A_command_given_the_id_of_a_personal_space_answers_not_found_and_changes_nothing(string command)   // criterion 14
    {
        var (user, space) = await CreatePrivateAccountAsync(Private);
        string[] args = command switch
        {
            "invite" => ["invite", "--org", space.ToString(), "--email", "new@poczta.test", "--role", "private"],
            "remove-member" => ["remove-member", "--org", space.ToString(), "--email", Private, "--force"],
            _ => ["delete-org", "--org", space.ToString(), "--confirm", "private"],
        };

        var run = await OperatorCli.RunAsync(Factory, args);

        Assert.Equal(AdminCli.Refused, run.Exit);
        Assert.Equal("error: not_found", run.Error.Trim());
        Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(c => c.Id == space, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == user && m.CompanyId == space, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await InDbAsync(db => db.Invites.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(0, await InDbAsync(db => db.MailRequests.CountAsync(TestContext.Current.CancellationToken)));
    }
}
