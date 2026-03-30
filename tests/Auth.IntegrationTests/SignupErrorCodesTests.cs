using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Http;

namespace Auth.IntegrationTests;

public sealed class SignupErrorCodesTests
{
    [Fact]
    public void The_codes_and_their_statuses_are_those_of_the_contract()
    {
        Assert.Equal("signup_disabled", TenancyErrors.SignupDisabled);
        Assert.Equal(StatusCodes.Status403Forbidden, TenancyErrors.StatusOf(TenancyErrors.SignupDisabled));
        Assert.Equal("already_registered", TenancyErrors.AlreadyRegistered);
        Assert.Equal(StatusCodes.Status409Conflict, TenancyErrors.StatusOf(TenancyErrors.AlreadyRegistered));
    }
}
