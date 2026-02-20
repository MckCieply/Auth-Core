using Auth.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Auth.IntegrationTests;

public sealed class HostHardeningTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    [Fact]
    public void Data_protection_keys_are_kept_in_memory_for_the_file_system_of_the_container_is_read_only()   // spec 0008 → Fixes
    {
        var provider = Factory.Services.GetRequiredService<IDataProtectionProvider>();

        Assert.Equal("EphemeralDataProtectionProvider", provider.GetType().Name);
    }

    [Fact]
    public void Kestrel_is_told_not_to_send_the_server_header()   // criterion 4: only a live stack shows the header itself (scripts/e2e-hardening.sh)
    {
        var options = Factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

        Assert.False(options.AddServerHeader);
    }
}
