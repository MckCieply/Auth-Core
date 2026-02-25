using Auth.Server.RateLimiting;
using Microsoft.AspNetCore.Http;

namespace Auth.IntegrationTests;

public sealed class RatePolicyTests
{
    [Theory]
    [InlineData("POST", "/auth/login", RatePolicy.Login)]
    [InlineData("POST", "/auth/refresh", RatePolicy.Refresh)]
    [InlineData("POST", "/auth/password/forgot", RatePolicy.Email)]
    [InlineData("POST", "/auth/password/reset", RatePolicy.Email)]
    [InlineData("POST", "/auth/email/verify/request", RatePolicy.Email)]
    [InlineData("POST", "/auth/email/verify", RatePolicy.Email)]
    [InlineData("POST", "/auth/invites/preview", RatePolicy.Invite)]
    [InlineData("POST", "/auth/invites/accept", RatePolicy.Invite)]
    [InlineData("POST", "/auth/logout", RatePolicy.General)]
    [InlineData("GET", "/auth/health", RatePolicy.General)]
    [InlineData("GET", "/auth/.well-known/jwks.json", RatePolicy.General)]
    [InlineData("GET", "/auth/me", RatePolicy.General)]
    [InlineData("PATCH", "/auth/org", RatePolicy.General)]
    [InlineData("POST", "/auth/org/invites", RatePolicy.General)]
    [InlineData("GET", "/auth/nothing/here", RatePolicy.General)]
    [InlineData("GET", "/auth", RatePolicy.General)]
    [InlineData("GET", "/auth/", RatePolicy.General)]
    public void A_request_counts_against_one_policy_by_its_method_and_path(string method, string path, RatePolicy expected) =>
        Assert.Equal(expected, RatePolicies.Classify(method, new PathString(path)));

    [Theory]
    [InlineData("GET", "/auth/login")]       // the policy is for the POST; any other method is an ordinary request
    [InlineData("HEAD", "/auth/refresh")]
    [InlineData("PUT", "/auth/password/forgot")]
    [InlineData("DELETE", "/auth/invites/accept")]
    public void The_method_matters(string method, string path) =>
        Assert.Equal(RatePolicy.General, RatePolicies.Classify(method, new PathString(path)));

    [Theory]
    [InlineData("/AUTH/Login")]
    [InlineData("/auth/login/")]
    [InlineData("/Auth/LOGIN/")]
    public void The_path_is_compared_without_regard_to_case_or_a_trailing_slash(string path) =>
        Assert.Equal(RatePolicy.Login, RatePolicies.Classify("POST", new PathString(path)));

    [Theory]
    [InlineData("GET", "/")]
    [InlineData("GET", "/nope")]
    [InlineData("POST", "/authx/login")]   // not under /auth/
    [InlineData("POST", "/api/auth/login")]
    public void A_request_outside_auth_is_not_limited(string method, string path) =>
        Assert.Null(RatePolicies.Classify(method, new PathString(path)));

    [Fact]
    public void The_names_and_defaults_are_those_of_the_contract()
    {
        Assert.Equal(["login", "refresh", "email", "invite", "general"], Enum.GetValues<RatePolicy>().Select(RatePolicies.NameOf));
        Assert.Equal([30, 60, 10, 20, 300], Enum.GetValues<RatePolicy>().Select(RatePolicies.DefaultPermitPerMinute));
    }
}
