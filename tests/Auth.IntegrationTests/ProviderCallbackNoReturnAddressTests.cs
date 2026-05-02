using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Providers;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → the callback on an instance without the return address: no round trip can have started, so there is no screen to send the browser to.</summary>
public sealed class ProviderCallbackNoReturnAddressTests : ProviderTestBase
{
    public ProviderCallbackNoReturnAddressTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithSetting(ProviderSettings.ReturnUrlKey, "");
    }

    [Theory]
    [InlineData("google")]
    [InlineData("microsoft")]
    public async Task A_callback_on_an_instance_without_the_return_address_is_404_not_found(string provider)
    {
        using var response = await CallbackAsync($"/auth/providers/{provider}/callback?code=x&state=y", null);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.NotFound, "not_found");
        Assert.Equal(0, await TicketCountAsync());
    }
}
