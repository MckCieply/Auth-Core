using Auth.Server.Login;
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
    [InlineData("/auth/login/", false)]
    [InlineData("/auth/login/extra", false)]
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
    [InlineData("refresh_token", "/auth/login", false)]
    [InlineData("password", "/auth/refresh", false)]
    [InlineData("refresh_token", "/auth/refresh", false)]
    public void ShouldShape_requires_a_password_grant_on_the_login_path(string grantType, string path, bool expected)
    {
        var request = new OpenIddictRequest { GrantType = grantType };

        Assert.Equal(expected, LoginResponseShaper.ShouldShape(request, new PathString(path)));
    }

    [Fact]
    public void ShouldShape_is_false_without_a_request() =>
        Assert.False(LoginResponseShaper.ShouldShape(null, new PathString("/auth/login")));
}
