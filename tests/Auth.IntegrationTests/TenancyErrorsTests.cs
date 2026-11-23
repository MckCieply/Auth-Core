using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Http;

namespace Auth.IntegrationTests;

public sealed class TenancyErrorsTests
{
    [Fact]
    public void The_three_codes_have_their_statuses()   // spec 0016
    {
        Assert.Equal("org_creation_disabled", TenancyErrors.OrgCreationDisabled);
        Assert.Equal(StatusCodes.Status403Forbidden, TenancyErrors.StatusOf(TenancyErrors.OrgCreationDisabled));
        Assert.Equal("company_member", TenancyErrors.CompanyMember);
        Assert.Equal(StatusCodes.Status403Forbidden, TenancyErrors.StatusOf(TenancyErrors.CompanyMember));
        Assert.Equal("company_limit_reached", TenancyErrors.CompanyLimitReached);
        Assert.Equal(StatusCodes.Status409Conflict, TenancyErrors.StatusOf(TenancyErrors.CompanyLimitReached));
        Assert.Throws<ArgumentOutOfRangeException>(() => TenancyErrors.StatusOf("no_such_code"));
    }
}
