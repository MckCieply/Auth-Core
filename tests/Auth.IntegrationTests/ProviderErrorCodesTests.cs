using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Http;

namespace Auth.IntegrationTests;

/// <summary>The error codes of spec 0011 and their statuses.</summary>
public sealed class ProviderErrorCodesTests
{
    [Theory]
    [InlineData(TenancyErrors.InvalidTicket, "invalid_ticket", StatusCodes.Status400BadRequest)]
    [InlineData(TenancyErrors.NoEmail, "no_email", StatusCodes.Status403Forbidden)]
    [InlineData(TenancyErrors.NoAccount, "no_account", StatusCodes.Status403Forbidden)]
    [InlineData(TenancyErrors.AddressMismatch, "address_mismatch", StatusCodes.Status403Forbidden)]
    [InlineData(TenancyErrors.ProviderAlreadyLinked, "provider_already_linked", StatusCodes.Status409Conflict)]
    [InlineData(TenancyErrors.PasswordRequired, "password_required", StatusCodes.Status403Forbidden)]
    [InlineData(TenancyErrors.ReauthRequired, "reauth_required", StatusCodes.Status403Forbidden)]
    [InlineData(TenancyErrors.ReauthFailed, "reauth_failed", StatusCodes.Status403Forbidden)]
    [InlineData(TenancyErrors.EmailNotVerified, "email_not_verified", StatusCodes.Status403Forbidden)]
    [InlineData(TenancyErrors.NoMembership, "no_membership", StatusCodes.Status403Forbidden)]
    public void The_codes_and_their_statuses_are_those_of_the_contract(string code, string name, int status)
    {
        Assert.Equal(name, code);
        Assert.Equal(status, TenancyErrors.StatusOf(code));
    }
}
