using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests;

public class ErrorHandlingTests : TenancyTestBase
{
    private bool _armed;

    public ErrorHandlingTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        // Once armed, the company API cannot read a membership: an exception from inside the endpoint, after authentication.
        Factory.WithServices(services => services.Replace(ServiceDescriptor.Scoped<MembershipReader>(provider =>
            _armed
                ? throw new InvalidOperationException("boom-for-the-test")
                : new MembershipReader(provider.GetRequiredService<AuthDbContext>(), provider.GetRequiredService<ManifestHolder>()))));
    }

    [Fact]
    public async Task An_unhandled_exception_is_a_500_internal_error_with_the_headers_and_is_logged()   // criterion 4
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        _armed = true;

        using var response = await TenancyApi.Get(Client, "/auth/me", token);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("""{"error":"internal_error"}""", raw);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        SecurityHeadersApi.AssertSecurityHeaders(response);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.DoesNotContain("boom-for-the-test", raw, StringComparison.Ordinal);   // nothing of the exception reaches the client
        Assert.Contains(
            Logs.Entries,
            e => e.Level == LogLevel.Error && e.Category.EndsWith("ErrorHandlingMiddleware", StringComparison.Ordinal)
                && e.Message.Contains("boom-for-the-test", StringComparison.Ordinal));   // the exception is logged
    }

    [Fact]
    public async Task The_refusals_of_the_service_are_not_taken_for_errors_by_the_handler()
    {
        _ = await CompanyWithAdminAsync();

        using var response = await SessionApi.Refresh(Client, "not-a-token");

        await SessionApi.AssertInvalidGrantAsync(response);   // still the uniform 401 of spec 0002, not a 500 or a 503
        SecurityHeadersApi.AssertSecurityHeaders(response);
    }
}

/// <summary>The same tests in Production, where the framework would otherwise show no developer page but a bare 500.</summary>
public sealed class ErrorHandlingProductionTests : ErrorHandlingTests
{
    public ErrorHandlingProductionTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithEnvironment("Production");
    }
}
