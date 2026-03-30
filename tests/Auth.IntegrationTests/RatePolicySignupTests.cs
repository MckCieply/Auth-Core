using Auth.Server.RateLimiting;
using Microsoft.AspNetCore.Http;

namespace Auth.IntegrationTests;

public sealed class RatePolicySignupTests
{
    [Theory]
    [InlineData("POST", "/auth/signup", RatePolicy.Email)]
    [InlineData("POST", "/auth/signup/preview", RatePolicy.Invite)]
    [InlineData("POST", "/auth/signup/complete", RatePolicy.Invite)]
    [InlineData("GET", "/auth/options", RatePolicy.General)]
    public void The_four_paths_count_against_the_policies_of_the_contract(string method, string path, RatePolicy expected)   // criterion 18
    {
        Assert.Equal(expected, RatePolicies.Classify(method, new PathString(path)));
    }

    [Theory]
    [InlineData("/AUTH/Signup", RatePolicy.Email)]
    [InlineData("/auth/signup/", RatePolicy.Email)]
    [InlineData("/Auth/SignUp/Preview/", RatePolicy.Invite)]
    [InlineData("/auth/SIGNUP/complete", RatePolicy.Invite)]
    public void The_paths_are_compared_without_regard_to_case_or_a_trailing_slash(string path, RatePolicy expected)
    {
        Assert.Equal(expected, RatePolicies.Classify("POST", new PathString(path)));
    }

    [Theory]
    [InlineData("GET", "/auth/signup")]
    [InlineData("PUT", "/auth/signup/preview")]
    [InlineData("DELETE", "/auth/signup/complete")]
    [InlineData("POST", "/auth/signup/other")]
    [InlineData("POST", "/auth/options")]
    public void Any_other_method_or_path_is_an_ordinary_request(string method, string path)
    {
        Assert.Equal(RatePolicy.General, RatePolicies.Classify(method, new PathString(path)));
    }
}
