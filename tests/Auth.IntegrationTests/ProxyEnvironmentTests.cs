using Auth.IntegrationTests.Infrastructure;

namespace Auth.IntegrationTests;

/// <summary>
/// What the test host pins blank so that a variable of the machine cannot make it trust a proxy. The helper takes the
/// environment as a list, so these tests never touch the process environment (test classes run in parallel).
/// </summary>
public sealed class ProxyEnvironmentTests
{
    private static Dictionary<string, string?> Pinned(params (string Name, string Value)[] environment) =>
        ProxyEnvironment.PinnedBlank(environment.Select(pair => new KeyValuePair<string, string>(pair.Name, pair.Value)));

    [Fact]
    public void The_list_form_of_a_proxy_variable_is_pinned_to_blank_under_its_configuration_key()
    {
        var pinned = Pinned(("Auth__Proxy__KnownNetworks__0", "10.250.0.0/24"), ("Auth__Proxy__KnownProxies__12", "10.0.0.7"));

        Assert.Equal(2, pinned.Count);
        Assert.Equal("", pinned["Auth:Proxy:KnownNetworks:0"]);
        Assert.Equal("", pinned["Auth:Proxy:KnownProxies:12"]);
    }

    [Fact]
    public void The_single_valued_form_is_pinned_too()
    {
        var pinned = Pinned(("Auth__Proxy__KnownNetworks", "10.250.0.0/24"));

        Assert.Equal("", pinned["Auth:Proxy:KnownNetworks"]);
    }

    [Fact]
    public void The_prefix_is_matched_without_regard_to_case()
    {
        var pinned = Pinned(("AUTH__PROXY__KNOWNNETWORKS__0", "10.250.0.0/24"));

        Assert.Equal("", Assert.Single(pinned).Value);
        Assert.Equal("AUTH:PROXY:KNOWNNETWORKS:0", Assert.Single(pinned).Key);
    }

    [Fact]
    public void A_name_written_with_colons_is_pinned_too()   // Windows allows a colon in a variable name
    {
        var pinned = Pinned(("Auth:Proxy:KnownNetworks:0", "10.250.0.0/24"), ("auth:proxy:KnownProxies", "10.0.0.7"));

        Assert.Equal(2, pinned.Count);
        Assert.Equal("", pinned["Auth:Proxy:KnownNetworks:0"]);
        Assert.Equal("", pinned["Auth:Proxy:KnownProxies"]);
    }

    [Theory]
    [InlineData("ASPNETCORE_")]
    [InlineData("DOTNET_")]
    [InlineData("aspnetcore_")]
    public void A_variable_the_host_reads_with_a_prefix_is_pinned_under_the_name_configuration_sees(string prefix)
    {
        var pinned = Pinned((prefix + "Auth__Proxy__KnownNetworks__0", "10.250.0.0/24"));

        Assert.Equal("", Assert.Single(pinned).Value);
        Assert.Equal("Auth:Proxy:KnownNetworks:0", Assert.Single(pinned).Key);
    }

    [Fact]
    public void Other_variables_are_left_alone()
    {
        var pinned = Pinned(("Auth__RateLimit__Enabled", "false"), ("Auth__Proxy", "x"), ("PATH", "/bin"), ("Auth_Proxy__X", "y"));

        Assert.Empty(pinned);
    }
}
