using Auth.Server.Providers;
using Microsoft.AspNetCore.Http;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011, Amendment A2: the callback paths, matched as OpenIddict matches its redirection endpoints.</summary>
public sealed class ProviderPathsTests
{
    [Theory]
    [InlineData("/auth/providers/google/callback", true)]
    [InlineData("/auth/providers/microsoft/callback", true)]
    [InlineData("/AUTH/Providers/GOOGLE/Callback", true)]
    [InlineData("/auth/providers/google/callback/", true)]
    [InlineData("/auth/providers/google/callback//", false)]
    [InlineData("/auth/providers/google/callback/extra", false)]
    [InlineData("/auth/providers/github/callback", false)]
    [InlineData("/auth/providers/google/start", false)]
    [InlineData("/auth/providers/exchange", false)]
    [InlineData("/", false)]
    public void IsCallbackPath_matches_the_callback_paths_only(string path, bool expected) =>
        Assert.Equal(expected, ProviderPaths.IsCallbackPath(new PathString(path)));

    [Fact]
    public void The_paths_are_those_of_the_contract()
    {
        Assert.Equal("/auth/providers/google/start", ProviderPaths.StartPathOf("google"));
        Assert.Equal("/auth/providers/microsoft/callback", ProviderPaths.CallbackPathOf("microsoft"));
        Assert.Equal(["/auth/providers/exchange", "/auth/providers/link", "/auth/providers/finish"], ProviderPaths.TokenPaths);
    }
}
