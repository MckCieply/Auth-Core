using Auth.Server.Requests;

namespace Auth.IntegrationTests;

public sealed class NameInputTests
{
    [Theory]
    [InlineData("Acme", true)]
    [InlineData("Acme sp. z o.o.", true)]
    [InlineData("Żółć & Spółka", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData(" Acme", false)]            // leading white space
    [InlineData("Acme ", false)]            // trailing white space
    [InlineData("\u00A0Acme", false)]       // a no-break space counts as white space
    [InlineData("Ac\u0009me", false)]       // a control character
    [InlineData("Ac\u000Ame", false)]
    [InlineData("Ac\u0085me", false)]
    [InlineData("Ac\uFFFEme", false)]       // a noncharacter
    [InlineData("Ac\uFDD0me", false)]
    public void Names_follow_the_rules(string name, bool valid)
    {
        Assert.Equal(valid, NameInput.IsValid(name));
    }

    [Fact]
    public void Name_may_be_100_characters_but_not_101()
    {
        Assert.True(NameInput.IsValid(new string('a', 100)));
        Assert.False(NameInput.IsValid(new string('a', 101)));
    }

    [Fact]
    public void Unpaired_surrogate_is_not_a_valid_name()
    {
        // Built at run time: an attribute argument cannot hold an unpaired surrogate.
        Assert.False(NameInput.IsValid("a" + (char)0xD800 + "b"));
        Assert.True(NameInput.IsValid("a\uD83D\uDE00b"));     // a well-formed pair
    }

    [Fact]
    public void Normalised_names_ignore_case()
    {
        Assert.Equal(NameInput.Normalize("Admin"), NameInput.Normalize("aDMIN"));
        Assert.Equal(NameInput.Normalize("zażółć"), NameInput.Normalize("ZAŻÓŁĆ"));
        Assert.NotEqual(NameInput.Normalize("admin"), NameInput.Normalize("admins"));
    }
}
