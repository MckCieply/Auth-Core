using Auth.Server.Login;
using Auth.Server.Providers;
using Auth.Server.Signup;
using Microsoft.AspNetCore.Http;
using OpenIddict.Abstractions;

namespace Auth.IntegrationTests;

/// <summary>
/// The login-specific handlers must act on <c>/auth/login</c> only, so spec 0002 can add <c>/auth/refresh</c> to the
/// token endpoint without being swallowed by them. Prod config has one endpoint, so the decision is tested directly.
/// </summary>
public sealed class LoginScopeTests
{
    [Theory]
    [InlineData("/auth/login", true)]
    [InlineData("/auth/LOGIN", true)]
    [InlineData("/auth/refresh", false)]
    [InlineData("/auth/login/", true)]
    [InlineData("/auth/LOGIN/", true)]
    [InlineData("/auth/login//", false)]
    [InlineData("/auth/login/extra", false)]
    [InlineData("/auth/signup/complete", false)]
    [InlineData("/", false)]
    public void IsLoginRequest_matches_the_login_path_only(string path, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        Assert.Equal(expected, JsonLoginRequestHandler.IsLoginRequest(context.Request));
    }

    [Fact]
    public void LoginPath_is_the_spec_path() => Assert.Equal("/auth/login", JsonLoginRequestHandler.LoginPath);

    [Theory]
    [InlineData("password", "/auth/login", true)]
    [InlineData("password", "/auth/LOGIN", true)]
    [InlineData("password", "/auth/login/", true)]
    [InlineData("password", "/auth/login//", false)]
    [InlineData("refresh_token", "/auth/login", false)]
    [InlineData("password", "/auth/refresh", false)]
    [InlineData("refresh_token", "/auth/refresh", false)]
    [InlineData("password", "/auth/signup/complete", true)]
    [InlineData("password", "/auth/SIGNUP/complete/", true)]
    [InlineData("password", "/auth/signup/complete//", false)]
    [InlineData("refresh_token", "/auth/signup/complete", false)]
    [InlineData("password", "/auth/providers/exchange", true)]   // spec 0011: the sign-ins through a provider answer as a login
    [InlineData("password", "/auth/providers/link", true)]
    [InlineData("password", "/auth/providers/finish", true)]
    [InlineData("password", "/AUTH/Providers/EXCHANGE", true)]
    [InlineData("password", "/auth/providers/link/", true)]
    [InlineData("password", "/auth/providers/exchange//", false)]
    [InlineData("password", "/auth/providers/finish/extra", false)]
    [InlineData("refresh_token", "/auth/providers/exchange", false)]
    [InlineData("password", "/auth/providers/google/callback", false)]
    public void ShouldShape_requires_a_password_grant_on_the_login_or_completion_path(string grantType, string path, bool expected)
    {
        var request = new OpenIddictRequest { GrantType = grantType };

        Assert.Equal(expected, LoginResponseShaper.ShouldShape(request, new PathString(path)));
    }

    [Fact]
    public void ShouldShape_is_false_without_a_request() =>
        Assert.False(LoginResponseShaper.ShouldShape(null, new PathString("/auth/login")));

    [Theory]
    [InlineData("/auth/signup/complete", true)]
    [InlineData("/auth/SIGNUP/COMPLETE", true)]
    [InlineData("/auth/signup/complete/", true)]
    [InlineData("/auth/signup/complete//", false)]
    [InlineData("/auth/signup/complete/extra", false)]
    [InlineData("/auth/signup", false)]
    [InlineData("/auth/login", false)]
    [InlineData("/", false)]
    public void IsCompletePath_matches_the_completion_path_only(string path, bool expected) =>
        Assert.Equal(expected, SignupCompleteRequestHandler.IsCompletePath(new PathString(path)));

    [Theory]
    [InlineData("/auth/providers/exchange", true)]
    [InlineData("/auth/providers/link", true)]
    [InlineData("/auth/providers/finish", true)]
    [InlineData("/AUTH/PROVIDERS/Finish", true)]
    [InlineData("/auth/providers/exchange/", true)]
    [InlineData("/auth/providers/exchange//", false)]
    [InlineData("/auth/providers/exchange/extra", false)]
    [InlineData("/auth/providers/google/start", false)]
    [InlineData("/auth/providers", false)]
    [InlineData("/auth/login", false)]
    public void IsProviderTokenPath_matches_the_three_provider_token_paths_only(string path, bool expected) =>   // spec 0011
        Assert.Equal(expected, ProviderRequestHandler.IsProviderTokenPath(new PathString(path)));
}
