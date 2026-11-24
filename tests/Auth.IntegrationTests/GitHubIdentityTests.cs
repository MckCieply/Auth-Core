using System.Text.Json;
using Auth.Server.Providers;
using Microsoft.AspNetCore.Identity;

namespace Auth.IntegrationTests;

/// <summary>Spec 0012 → The identity (criterion 4), one test per rule: the key is <c>id</c>; the address is the primary entry only.</summary>
public sealed class GitHubIdentityTests
{
    private const string User = """{"login":"ola-gh","id":70000001,"email":"ola.public@github.test"}""";
    private const string Primary = """[{"email":"ola@github.test","primary":true,"verified":true,"visibility":"private"}]""";

    private static readonly UpperInvariantLookupNormalizer Normalizer = new();

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static GitHubAccount? Read(string user, string emails) => GitHubIdentity.Read(Json(user), Json(emails));

    [Fact]
    public void The_key_is_the_id_as_a_decimal_string_and_never_the_login()   // criterion 4
    {
        Assert.Equal("70000001", Read(User, Primary)!.Key);
        Assert.Equal("7", Read("""{"login":"12345","id":7}""", Primary)!.Key);
        Assert.Equal("9007199254740993", Read("""{"id":9007199254740993}""", Primary)!.Key);   // int64, not a double
    }

    [Fact]
    public void Only_the_primary_entry_is_the_address_and_a_verified_secondary_is_never_used()   // criterion 4, Decision 2, Review Focus 4
    {
        var account = Read(
            User,
            """
            [{"email":"ola.work@firma.test","primary":false,"verified":true},
             {"email":"ola@github.test","primary":true,"verified":false},
             {"email":"ola.old@github.test","primary":false,"verified":true}]
            """);

        Assert.Equal(new GitHubAccount("70000001", "ola@github.test", false), account);
    }

    [Fact]
    public void A_primary_entry_with_verified_false_is_unverified_and_true_is_verified()   // criterion 4
    {
        Assert.False(Read(User, """[{"email":"kim@github.test","primary":true,"verified":false}]""")!.EmailVerified);
        Assert.True(Read(User, Primary)!.EmailVerified);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""[{"email":"lee@github.test","primary":false,"verified":true},{"email":"lee@work.test","primary":false,"verified":true}]""")]
    [InlineData("""[{"email":"a@github.test","primary":true,"verified":true},{"email":"b@github.test","primary":true,"verified":true}]""")]
    public void No_primary_entry_or_more_than_one_is_no_address(string emails)   // criterion 4
    {
        Assert.Equal(new GitHubAccount("70000001", null, false), Read(User, emails));
    }

    [Fact]
    public void The_email_of_user_is_never_the_address()   // spec 0012 → The identity: never `email` of /user
    {
        var account = Read(User, """[{"email":"lee@github.test","primary":false,"verified":true}]""");

        Assert.Null(account!.Email);
        Assert.NotEqual("ola.public@github.test", Read(User, Primary)!.Email);
    }

    [Theory]
    [InlineData("ola@localhost")]
    [InlineData("ola@10.0.0.1")]
    [InlineData("ola@intranet.123")]
    public void An_address_that_breaks_the_invitation_rule_is_kept_and_unverified(string email)   // criterion 4
    {
        var identity = ProviderIdentities.FromGitHub(new GitHubAccount("70000001", email, true), Normalizer);

        Assert.Equal((ProviderNames.GitHub, "70000001", email, false), (identity.Provider, identity.Key, identity.Email, identity.EmailVerified));
        Assert.True(ProviderIdentities.FromGitHub(new GitHubAccount("70000001", "ola@github.test", true), Normalizer).EmailVerified);
        Assert.Null(ProviderIdentities.FromGitHub(new GitHubAccount("70000001", null, false), Normalizer).Email);
    }

    [Theory]
    [InlineData("""{"login":"ola-gh"}""")]
    [InlineData("""{"id":0}""")]
    [InlineData("""{"id":-1}""")]
    [InlineData("""{"id":1.5}""")]
    [InlineData("""{"id":1e3}""")]
    [InlineData("""{"id":"70000001"}""")]
    [InlineData("""{"id":null}""")]
    [InlineData("""{"id":99999999999999999999}""")]
    public void A_user_without_an_id_or_with_one_that_is_not_a_positive_integer_is_refused(string user)   // criterion 5's input
    {
        Assert.Null(Read(user, Primary));
    }

    [Theory]
    [InlineData("""[{"email":5,"primary":true,"verified":true}]""")]
    [InlineData("""[{"email":null,"primary":true,"verified":true}]""")]
    [InlineData("""[{"primary":true,"verified":true}]""")]
    [InlineData("""[{"email":"a@github.test","primary":"yes","verified":true}]""")]
    [InlineData("""[{"email":"a@github.test","verified":true}]""")]
    [InlineData("""[{"email":"a@github.test","primary":true,"verified":"true"}]""")]
    [InlineData("""[{"email":"a@github.test","primary":true}]""")]
    [InlineData("""["a@github.test"]""")]
    public void An_entry_of_the_wrong_shape_refuses_the_whole_answer(string emails)   // spec 0012 → The identity: not the documented JSON
    {
        Assert.Null(Read(User, emails));
    }

    [Fact]
    public void An_address_that_cannot_be_read_as_text_is_no_address_and_not_an_exception()   // an unpaired surrogate escape: the JSON parses, the text does not
    {
        var emails = """[{"email":"{B}ud800","primary":true,"verified":true}]""".Replace("{B}", ((char)92).ToString(), StringComparison.Ordinal);

        Assert.Equal(new GitHubAccount("70000001", null, false), Read(User, emails));
    }

    [Fact]
    public void Answers_that_are_not_an_object_and_an_array_are_refused()
    {
        Assert.Null(Read("[]", Primary));
        Assert.Null(Read(User, "{}"));
        Assert.Null(Read("\"ola\"", Primary));
    }

    [Fact]
    public void The_items_carry_the_identity_and_read_back_only_a_decimal_key()   // Amendment A3: the hand-over to the callback
    {
        var items = new Dictionary<string, string?>(StringComparer.Ordinal) { ["purpose"] = "sign_in" };

        GitHubItems.Write(items, new GitHubAccount("70000001", "ola@github.test", true));

        Assert.Equal(new GitHubAccount("70000001", "ola@github.test", true), GitHubItems.Read(items));
        Assert.Equal(4, items.Count);
        GitHubItems.Write(items, new GitHubAccount("70000002", null, false));
        Assert.Equal(new GitHubAccount("70000002", null, false), GitHubItems.Read(items));
        items[GitHubItems.Key] = "ola-gh";
        Assert.Null(GitHubItems.Read(items));
        Assert.Null(GitHubItems.Read(null));
        Assert.Null(GitHubItems.Read(new Dictionary<string, string?>(StringComparer.Ordinal)));
    }
}
