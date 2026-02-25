using System.Net;
using System.Text.Json.Nodes;
using Auth.IntegrationTests.Infrastructure;

namespace Auth.IntegrationTests;

/// <summary>What spec 0008 adds to the OpenAPI description (the rest of it is in <see cref="OpenApiTests"/>).</summary>
public sealed class OpenApiHardeningTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    private async Task<(JsonObject Document, Dictionary<string, JsonObject> Operations)> DescriptionAsync()
    {
        using var response = await Client.GetAsync("/auth/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        var operations = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (path, item) in document["paths"]!.AsObject())
        {
            foreach (var (method, operation) in item!.AsObject())
            {
                operations[$"{method.ToUpperInvariant()} {path}"] = operation!.AsObject();
            }
        }

        return (document, operations);
    }

    private static string Description(JsonObject operation, string status) => operation["responses"]![status]!["description"]!.GetValue<string>();

    [Fact]
    public async Task The_company_deletion_is_described_with_its_body_and_its_answers()   // criterion 10
    {
        var (document, operations) = await DescriptionAsync();

        var operation = operations["DELETE /auth/org"];

        var schema = operation["requestBody"]!["content"]!["application/json"]!["schema"]!.AsObject();
        if (schema["$ref"] is { } reference)
        {
            schema = document["components"]!["schemas"]![reference.GetValue<string>().Split('/')[^1]]!.AsObject();
        }

        Assert.Equal(["name", "password"], schema["properties"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["204", "400", "401", "403", "415", "429"], operation["responses"]!.AsObject().Select(r => r.Key).Order(StringComparer.Ordinal));
        Assert.All(["forbidden", "permissions_changed", "permission_not_held", "wrong_password"], code => Assert.Contains(code, Description(operation, "403")));
        Assert.Contains("invalid_request", Description(operation, "400"));
        Assert.All(["too_many_attempts", "too_many_requests"], code => Assert.Contains(code, Description(operation, "429")));
        Assert.NotNull(operation["security"]);
    }

    [Fact]
    public async Task The_permission_org_delete_is_named_in_the_description_and_in_the_operation()
    {
        var (document, operations) = await DescriptionAsync();

        Assert.Contains("org:delete", document["info"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("org:delete", operations["DELETE /auth/org"]["description"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_operation_can_say_429_too_many_requests_with_retry_after_the_two_added_by_hand_included()   // spec 0008 → OpenAPI
    {
        var (_, operations) = await DescriptionAsync();

        Assert.Equal(26, operations.Count);
        foreach (var (endpoint, operation) in operations)
        {
            Assert.Contains("too_many_requests", Description(operation, "429"));
            var header = operation["responses"]!["429"]!["headers"]!["Retry-After"]!;
            Assert.Equal("integer", header["schema"]!["type"]!.GetValue<string>());
            Assert.True(operation["responses"]!["429"]!["content"]!["application/json"]!["schema"] is not null, endpoint);
        }
    }

    [Fact]
    public async Task Exactly_the_operations_the_charset_guard_covers_say_415_unsupported_media_type()   // spec 0008 → Fixes and OpenAPI
    {
        var (_, operations) = await DescriptionAsync();

        var covered = 0;
        foreach (var (endpoint, operation) in operations)
        {
            // The scope of the guard: POST, PUT, PATCH and DELETE under /auth/ other than refresh and logout, whether or not the route reads a body.
            var parts = endpoint.Split(' ', 2);
            var guarded = parts[0] is "POST" or "PUT" or "PATCH" or "DELETE" && parts[1] is not ("/auth/refresh" or "/auth/logout");
            Assert.Equal(guarded, operation["responses"]!["415"] is not null);
            if (guarded)
            {
                covered++;
                Assert.Contains("unsupported_media_type", Description(operation, "415"));
            }

            if (operation["requestBody"] is not null)
            {
                Assert.True(guarded, endpoint + " reads a JSON body, so the guard covers it");
            }
        }

        Assert.Equal(17, covered);
    }

    [Fact]
    public async Task Refresh_can_say_503_temporarily_unavailable_with_retry_after_and_no_other_endpoint_can()   // spec 0008 → OpenAPI
    {
        var (_, operations) = await DescriptionAsync();

        var refresh = operations["POST /auth/refresh"];
        Assert.Contains("temporarily_unavailable", Description(refresh, "503"));
        Assert.NotNull(refresh["responses"]!["503"]!["headers"]!["Retry-After"]);
        Assert.Equal(["POST /auth/refresh"], operations.Where(o => o.Value["responses"]!["503"] is not null).Select(o => o.Key));
    }

    [Fact]
    public async Task The_health_check_and_the_key_set_are_described_with_the_429_and_no_other_error()
    {
        var (_, operations) = await DescriptionAsync();

        foreach (var endpoint in new[] { "GET /auth/health", "GET /auth/.well-known/jwks.json" })
        {
            Assert.Equal(["200", "429"], operations[endpoint]["responses"]!.AsObject().Select(r => r.Key).Order(StringComparer.Ordinal));
        }
    }
}
