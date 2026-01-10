using System.Net;
using System.Text;
using System.Text.Json;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Server;

namespace Auth.IntegrationTests;

/// <summary>
/// Production has a single token endpoint (<c>/auth/login</c>), so a token-endpoint request that no extraction
/// handler claims cannot be produced against the real configuration. This host registers a second token endpoint
/// that nothing extracts a request for, to prove such a request is a 400 and never a 500.
/// </summary>
public sealed class UnhandledTokenRequestGuardTests : IAsyncLifetime
{
    private readonly ExtraTokenEndpointFactory _factory;
    private HttpClient _client = null!;

    public UnhandledTokenRequestGuardTests(PostgresFixture postgres, KeyMaterialFixture keys)
    {
        _factory = new ExtraTokenEndpointFactory(postgres, keys);
    }

    public ValueTask InitializeAsync()
    {
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Post_to_a_token_endpoint_nothing_extracts_returns_400_invalid_request()
    {
        using var response = await _client.PostAsync(
            "/auth/other", new StringContent("""{"email":"a@example.com","password":"x"}""", Encoding.UTF8, "application/json"));

        await AssertInvalidRequestAsync(response);
    }

    [Fact]
    public async Task Get_to_a_token_endpoint_nothing_extracts_returns_400_invalid_request()
    {
        using var response = await _client.GetAsync("/auth/other");

        await AssertInvalidRequestAsync(response);
    }

    private static async Task AssertInvalidRequestAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"Expected 400, got {(int)response.StatusCode}: {raw}");
        using var json = JsonDocument.Parse(raw);
        Assert.Equal("invalid_request", json.RootElement.GetProperty("error").GetString());
    }

    private sealed class ExtraTokenEndpointFactory(PostgresFixture postgres, KeyMaterialFixture keys) : AuthAppFactory(postgres, keys)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
                services.Configure<OpenIddictServerOptions>(o => o.TokenEndpointUris.Add(new Uri("auth/other", UriKind.Relative))));
        }
    }
}
