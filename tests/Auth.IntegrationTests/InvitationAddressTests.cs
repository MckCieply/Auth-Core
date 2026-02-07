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
    [InlineData("joe@intranet.\u00AD")]     // a soft hyphen: IDNA drops it, and intranet. is sent
    [InlineData("joe@intranet.\u200B")]     // a zero-width space, dropped likewise
    [InlineData("joe@localhost.\uFE0F")]    // a variation selector, dropped likewise
    [InlineData("joe@10.0.0.\uFF15")]       // a fullwidth 5: IDNA maps it, and 10.0.0.5 is sent
    [InlineData("joe@10.0.0.5\u00AD")]
    [InlineData("joe@10.0.0\u30025")]       // an ideographic full stop: IDNA maps it to a dot
    [InlineData("joe@intranet\uFF0Etest")]  // a fullwidth full stop
    public void A_domain_that_becomes_an_internal_host_on_its_way_to_the_relay_is_not_invitable(string email)
    {
        Assert.False(EmailInput.IsInvitable(email));
    }

    [Theory]
    [InlineData("Steve@acme.test", "steve@ACME.TEST")]
    [InlineData("a@b.c", "A@B.C")]
    public void Addresses_that_differ_in_ascii_capitals_only_are_the_same(string one, string other)
    {
        Assert.True(EmailInput.IsSameAddress(one, other));
        Assert.True(EmailInput.IsSameAddress(other, one));
    }

    [Theory]
    [InlineData("\u03BCaria@corp.test", "\u00B5aria@corp.test")]   // Greek mu, the micro sign
    [InlineData("kosta\u03C2@corp.test", "kosta\u03C3@corp.test")]  // final sigma, sigma
    [InlineData("\u0441ergey@corp.test", "\u1C83ergey@corp.test")]  // Cyrillic es, its narrow form
    [InlineData("steve@corp.test", "\u017Fteve@corp.test")]              // the long s
    [InlineData("kate@corp.test", "\u212Aate@corp.test")]                // the Kelvin sign
    [InlineData("g\u00F3rski@corp.test", "go\u0301rski@corp.test")] // composed, decomposed
    [InlineData("\u017Baneta@corp.test", "\u017Caneta@corp.test")] // capitals beyond ASCII are not folded
    [InlineData("steve@corp.test", "steve@corp.tes")]
    public void Addresses_that_differ_in_anything_else_are_not_the_same(string one, string other)
    {
        Assert.False(EmailInput.IsSameAddress(one, other));
        Assert.False(EmailInput.IsSameAddress(other, one));
    }

    [Theory]
    [InlineData("joe@acme.test")]
    [InlineData("joe@\u017C\u00F3\u0142w.pl")]   // an international domain name
    [InlineData("joe@xn--w-uga1v8h.pl")]
    [InlineData("joe@mail.acme.co.uk")]
    [InlineData("joe@1and1.example")]   // a digit in a name is fine; only a last part of digits is an address
    public void An_address_with_a_dotted_domain_name_is_invitable(string email)
    {
        Assert.True(EmailInput.IsInvitable(email));
    }
}
