using Auth.Server.Requests;

namespace Auth.IntegrationTests;

public sealed class InvitationAddressTests
{
    [Theory]
    [InlineData("\u017Fteve@corp.test")]     // a long s, whose upper case is S
    [InlineData("\u212Aate@corp.test")]      // the Kelvin sign, whose lower case is k
    [InlineData("\u0130van@corp.test")]      // a dotted capital I
    [InlineData("v\u0131ctor@corp.test")]    // a dotless small i
    [InlineData("steve@corp.te\u017Ft")]     // in the domain too
    public void An_address_with_a_letter_that_folds_into_ascii_may_stand_for_another(string email)
    {
        Assert.True(EmailInput.MayStandForAnotherAddress(email));
        Assert.False(EmailInput.IsInvitable(email));
    }

    [Theory]
    [InlineData("steve@corp.test")]
    [InlineData("STEVE@CORP.TEST")]
    [InlineData("zażółć.gęślą@corp.test")]       // letters beyond ASCII whose cases are not ASCII
    [InlineData("Łukasz@corp.test")]
    [InlineData("stra\u00DFe@corp.test")]    // a sharp s has no one-letter upper case
    public void An_ordinary_address_stands_for_itself_and_is_invitable(string email)
    {
        Assert.False(EmailInput.MayStandForAnotherAddress(email));
        Assert.True(EmailInput.IsInvitable(email));
    }

    [Theory]
    [InlineData("joe@[10.0.0.5]")]      // a domain literal
    [InlineData("joe@[IPv6:::1]")]
    [InlineData("joe@localhost")]       // no dot
    [InlineData("joe@intranet")]
    [InlineData("joe@localhost.")]      // a dot only at the end is still one name
    [InlineData("joe@10.0.0.5")]        // an IP address without its brackets
    [InlineData("joe")]                 // no domain at all
    public void An_address_whose_domain_could_be_an_internal_host_is_not_invitable(string email)
    {
        Assert.False(EmailInput.IsInvitable(email));
    }

    [Theory]
    [InlineData("joe@acme.test")]
    [InlineData("joe@mail.acme.co.uk")]
    [InlineData("joe@1and1.example")]   // a digit in a name is fine; only a last part of digits is an address
    public void An_address_with_a_dotted_domain_name_is_invitable(string email)
    {
        Assert.True(EmailInput.IsInvitable(email));
    }
}
