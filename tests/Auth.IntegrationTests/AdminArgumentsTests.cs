using Auth.Server.Admin;

namespace Auth.IntegrationTests;

public sealed class AdminArgumentsTests
{
    private static AdminCommand Command(params string[] args)
    {
        var result = AdminArguments.Parse(args);

        Assert.Null(result.Error);
        return Assert.IsAssignableFrom<AdminCommand>(result.Command);
    }

    private static string Error(params string[] args)
    {
        var result = AdminArguments.Parse(args);

        Assert.Null(result.Command);
        return Assert.IsType<string>(result.Error);
    }

    [Fact]
    public void Create_org_takes_a_name()   // criterion 1
    {
        Assert.Equal(new CreateOrgCommand("Acme"), Command("create-org", "--name", "Acme"));
        Assert.Equal(new CreateOrgCommand("Acme sp. z o.o."), Command("create-org", "--name=Acme sp. z o.o."));
    }

    [Fact]
    public void Invite_takes_an_org_an_email_and_a_role_in_any_order()
    {
        var expected = new InviteCommand("11111111-1111-1111-1111-111111111111", "boss@acme.test", "admin");

        Assert.Equal(expected, Command("invite", "--org", "11111111-1111-1111-1111-111111111111", "--email", "boss@acme.test", "--role", "admin"));
        Assert.Equal(expected, Command("invite", "--role=admin", "--email=boss@acme.test", "--org=11111111-1111-1111-1111-111111111111"));
    }

    [Fact]
    public void List_orgs_takes_nothing()
    {
        Assert.Equal(new ListOrgsCommand(), Command("list-orgs"));
    }

    [Fact]
    public void Remove_member_takes_an_org_an_email_and_optionally_force()   // criterion 22
    {
        Assert.Equal(
            new RemoveMemberCommand("11111111-1111-1111-1111-111111111111", "a@acme.test", false),
            Command("remove-member", "--org", "11111111-1111-1111-1111-111111111111", "--email", "a@acme.test"));
        Assert.Equal(
            new RemoveMemberCommand("11111111-1111-1111-1111-111111111111", "a@acme.test", true),
            Command("remove-member", "--force", "--org", "11111111-1111-1111-1111-111111111111", "--email=a@acme.test"));
    }

    [Fact]
    public void A_value_may_hold_an_equals_sign_and_be_empty()
    {
        Assert.Equal(new CreateOrgCommand("a=b"), Command("create-org", "--name=a=b"));
        Assert.Equal(new CreateOrgCommand(""), Command("create-org", "--name="));   // refused later, as a name
    }

    [Fact]
    public void No_command_and_an_unknown_command_are_errors()
    {
        Assert.Equal("missing_command", Error());
        Assert.Equal("unknown_command", Error("delete-org"));
        Assert.Equal("unknown_command", Error("--name", "Acme"));
        Assert.Equal("unknown_command", Error("Create-Org", "--name", "Acme"));
    }

    [Fact]
    public void An_option_the_command_does_not_have_is_an_error()
    {
        Assert.Equal("unknown_option", Error("create-org", "--name", "Acme", "--force"));
        Assert.Equal("unknown_option", Error("invite", "--org", "x", "--email", "y", "--role", "z", "--force"));
        Assert.Equal("unknown_option", Error("list-orgs", "--all"));
        Assert.Equal("unknown_option", Error("remove-member", "--org", "x", "--email", "y", "--role", "z"));
    }

    [Fact]
    public void A_missing_option_is_an_error()
    {
        Assert.Equal("missing_option", Error("create-org"));
        Assert.Equal("missing_option", Error("invite", "--org", "x", "--email", "y"));
        Assert.Equal("missing_option", Error("remove-member", "--force", "--org", "x"));
    }

    [Fact]
    public void An_option_without_a_value_is_an_error()
    {
        Assert.Equal("missing_value", Error("create-org", "--name"));
        Assert.Equal("missing_value", Error("invite", "--org", "--email", "y", "--role", "z"));   // the next word is an option, not a value
    }

    [Fact]
    public void A_repeated_option_is_an_error()
    {
        Assert.Equal("duplicate_option", Error("create-org", "--name", "A", "--name", "B"));
        Assert.Equal("duplicate_option", Error("remove-member", "--org", "x", "--email", "y", "--force", "--force"));
    }

    [Fact]
    public void A_flag_takes_no_value_and_a_word_without_dashes_is_not_expected()
    {
        Assert.Equal("unknown_option", Error("remove-member", "--org", "x", "--email", "y", "--force=yes"));
        Assert.Equal("unexpected_argument", Error("create-org", "Acme"));
        Assert.Equal("unexpected_argument", Error("list-orgs", "now"));
        Assert.Equal("unexpected_argument", Error("create-org", "--name", "A", "B"));
    }
}
