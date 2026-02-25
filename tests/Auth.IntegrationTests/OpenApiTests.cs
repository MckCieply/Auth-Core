using System.Net;
using System.Text.Json.Nodes;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Api;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class OpenApiTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private const string DocumentUrl = "/auth/openapi/v1.json";

    // Every endpoint of specs 0001–0008, as "METHOD path".
    private static readonly string[] Endpoints =
    [
        "POST /auth/login", "POST /auth/refresh", "POST /auth/logout",
        "POST /auth/password/forgot", "POST /auth/password/reset", "POST /auth/email/verify/request", "POST /auth/email/verify",
        "GET /auth/.well-known/jwks.json", "GET /auth/health",
        "GET /auth/me",
        "POST /auth/invites/preview", "POST /auth/invites/accept",
        "GET /auth/org", "PATCH /auth/org",
        "DELETE /auth/org",
        "GET /auth/org/members", "PUT /auth/org/members/{user_id}/role", "DELETE /auth/org/members/{user_id}",
        "GET /auth/org/invites", "POST /auth/org/invites", "POST /auth/org/invites/{id}/resend", "DELETE /auth/org/invites/{id}",
        "GET /auth/org/roles", "POST /auth/org/roles", "PUT /auth/org/roles/{id}", "DELETE /auth/org/roles/{id}",
    ];

    private static async Task<JsonObject> DescriptionAsync(AuthAppFactory factory)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(DocumentUrl);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    private static Dictionary<string, JsonObject> Operations(JsonObject document)
    {
        var operations = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (path, item) in document["paths"]!.AsObject())
        {
            foreach (var (method, operation) in item!.AsObject())
            {
                operations[$"{method.ToUpperInvariant()} {path}"] = operation!.AsObject();
            }
        }

        return operations;
    }

    /// <summary>The path of a route as the description writes it: a group's empty route has no trailing slash there.</summary>
    private static string PathOf(RouteEndpoint route) => route.RoutePattern.RawText!.TrimEnd('/');

    private static string[] Statuses(JsonObject operation) => [.. operation["responses"]!.AsObject().Select(r => r.Key).Order(StringComparer.Ordinal)];

    private static string Description(JsonObject operation, string status) =>
        operation["responses"]![status]!["description"]!.GetValue<string>();

    private static string[] BodyProperties(JsonObject document, JsonObject operation)
    {
        var schema = operation["requestBody"]!["content"]!["application/json"]!["schema"]!.AsObject();
        if (schema["$ref"] is { } reference)
        {
            schema = document["components"]!["schemas"]![reference.GetValue<string>().Split('/')[^1]]!.AsObject();
        }

        return [.. schema["properties"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task The_description_is_served_without_a_token_in_every_environment()   // criterion 24
    {
        foreach (var environment in new[] { "Development", "Production" })
        {
            await using var factory = new AuthAppFactory(postgres, keys).WithEnvironment(environment);

            var document = await DescriptionAsync(factory);

            Assert.StartsWith("3.", document["openapi"]!.GetValue<string>());
            Assert.Equal("Auth-Core", document["info"]!["title"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task The_description_names_every_endpoint_of_the_service_and_nothing_else()   // criterion 24
    {
        await using var factory = new AuthAppFactory(postgres, keys);

        var operations = Operations(await DescriptionAsync(factory));

        Assert.Equal(Endpoints.Order(StringComparer.Ordinal), operations.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Every_route_of_the_service_is_in_the_description()   // criterion 24: a new endpoint without a description fails here
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        var operations = Operations(await DescriptionAsync(factory));
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => PathOf(e).StartsWith("/auth/", StringComparison.Ordinal)
                && !PathOf(e).StartsWith("/auth/openapi", StringComparison.Ordinal)
                && !PathOf(e).StartsWith("/auth/scalar", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(routes);
        foreach (var route in routes)
        {
            // HEAD answers wherever GET does (the health check): the description says it once, under GET.
            var methods = (route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"]).Where(m => m != "HEAD");
            foreach (var method in methods)
            {
                Assert.True(operations.ContainsKey($"{method} {PathOf(route)}"), $"{method} {PathOf(route)} is not described.");
            }
        }
    }

    [Fact]
    public async Task Every_error_status_carries_the_codes_the_endpoint_declares()   // criterion 24
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        var operations = Operations(await DescriptionAsync(factory));
        var declared = 0;

        foreach (var route in factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var codes = route.Metadata.GetOrderedMetadata<ErrorCodesMetadata>();
            if (codes.Count == 0)
            {
                continue;
            }

            var method = route.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single();
            var operation = operations[$"{method} {PathOf(route)}"];
            foreach (var entry in codes)
            {
                foreach (var code in entry.Codes)
                {
                    Assert.Contains(code, Description(operation, entry.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    declared++;
                }
            }
        }

        Assert.True(declared > 60, $"Only {declared} error codes were declared.");
    }

    [Theory]
    // Every operation can also say 429 (the per-IP limit), and every POST, PUT, PATCH and DELETE but refresh and logout 415 (spec 0008: the charset guard).
    [InlineData("POST /auth/login", "200,400,401,403,415,429")]
    [InlineData("POST /auth/refresh", "200,401,429,503")]
    [InlineData("POST /auth/logout", "204,429")]
    [InlineData("POST /auth/password/forgot", "202,400,415,429")]
    [InlineData("POST /auth/password/reset", "204,400,415,429")]
    [InlineData("POST /auth/email/verify/request", "202,400,415,429")]
    [InlineData("POST /auth/email/verify", "204,400,415,429")]
    [InlineData("GET /auth/me", "200,401,403,429")]
    [InlineData("POST /auth/invites/preview", "200,400,409,415,429")]
    [InlineData("POST /auth/invites/accept", "204,400,409,415,429")]
    [InlineData("GET /auth/org", "200,401,403,429")]
    [InlineData("PATCH /auth/org", "204,400,401,403,415,429")]
    [InlineData("DELETE /auth/org", "204,400,401,403,415,429")]
    [InlineData("GET /auth/org/members", "200,401,403,429")]
    [InlineData("PUT /auth/org/members/{user_id}/role", "204,400,401,403,404,409,415,429")]
    [InlineData("DELETE /auth/org/members/{user_id}", "204,400,401,403,404,409,415,429")]
    [InlineData("GET /auth/org/invites", "200,401,403,429")]
    [InlineData("POST /auth/org/invites", "202,400,401,403,404,409,415,429")]
    [InlineData("POST /auth/org/invites/{id}/resend", "202,400,401,403,404,415,429")]
    [InlineData("DELETE /auth/org/invites/{id}", "204,400,401,403,404,415,429")]
    [InlineData("GET /auth/org/roles", "200,401,403,429")]
    [InlineData("POST /auth/org/roles", "201,400,401,403,409,415,429")]
    [InlineData("PUT /auth/org/roles/{id}", "204,400,401,403,404,409,415,429")]
    [InlineData("DELETE /auth/org/roles/{id}", "204,400,401,403,404,409,415,429")]
    public async Task Each_endpoint_describes_the_statuses_of_the_contract(string endpoint, string statuses)   // criterion 24
    {
        await using var factory = new AuthAppFactory(postgres, keys);

        var operation = Operations(await DescriptionAsync(factory))[endpoint];

        Assert.Equal(statuses.Split(','), Statuses(operation));
    }

    [Fact]
    public async Task The_error_codes_of_the_contract_are_in_the_descriptions()   // criterion 24
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        var operations = Operations(await DescriptionAsync(factory));

        Assert.All(["email_not_verified", "no_membership"], code => Assert.Contains(code, Description(operations["POST /auth/login"], "403")));
        Assert.Contains("invalid_credentials", Description(operations["POST /auth/login"], "401"));
        Assert.Contains("too_many_attempts", Description(operations["POST /auth/login"], "429"));
        Assert.Contains("invalid_grant", Description(operations["POST /auth/refresh"], "401"));
        Assert.All(["invalid_request", "invalid_token", "weak_password"], code => Assert.Contains(code, Description(operations["POST /auth/invites/accept"], "400")));
        Assert.Contains("already_member", Description(operations["POST /auth/invites/accept"], "409"));
        Assert.All(["permissions_changed", "forbidden"], code => Assert.Contains(code, Description(operations["GET /auth/org/members"], "403")));
        Assert.All(["already_in_org", "invite_pending"], code => Assert.Contains(code, Description(operations["POST /auth/org/invites"], "409")));
        Assert.Contains("permission_not_held", Description(operations["POST /auth/org/invites"], "403"));
        Assert.All(["cannot_change_self", "last_manager"], code => Assert.Contains(code, Description(operations["DELETE /auth/org/members/{user_id}"], "409")));
        Assert.All(["role_name_taken", "last_manager"], code => Assert.Contains(code, Description(operations["PUT /auth/org/roles/{id}"], "409")));
        Assert.Contains("role_in_use", Description(operations["DELETE /auth/org/roles/{id}"], "409"));
        Assert.Contains("unknown_permission", Description(operations["POST /auth/org/roles"], "400"));
        Assert.Contains("not_found", Description(operations["DELETE /auth/org/invites/{id}"], "404"));
    }

    [Theory]
    [InlineData("POST /auth/org/invites")]
    [InlineData("POST /auth/org/invites/{id}/resend")]
    [InlineData("DELETE /auth/org/invites/{id}")]
    [InlineData("PUT /auth/org/members/{user_id}/role")]
    [InlineData("DELETE /auth/org/members/{user_id}")]
    [InlineData("POST /auth/org/roles")]
    [InlineData("PUT /auth/org/roles/{id}")]
    [InlineData("DELETE /auth/org/roles/{id}")]
    [InlineData("DELETE /auth/org")]
    public async Task Each_endpoint_under_safety_rule_1_lists_permission_not_held_under_403(string endpoint)   // criterion 24
    {
        await using var factory = new AuthAppFactory(postgres, keys);

        var operation = Operations(await DescriptionAsync(factory))[endpoint];

        Assert.Contains("permission_not_held", Description(operation, "403"));
    }

    [Theory]
    [InlineData("POST /auth/login", "email,password")]
    [InlineData("POST /auth/password/forgot", "email")]
    [InlineData("POST /auth/password/reset", "new_password,token")]
    [InlineData("POST /auth/email/verify/request", "email")]
    [InlineData("POST /auth/email/verify", "token")]
    [InlineData("POST /auth/invites/preview", "token")]
    [InlineData("POST /auth/invites/accept", "password,token")]
    [InlineData("PATCH /auth/org", "name")]
    [InlineData("DELETE /auth/org", "name,password")]
    [InlineData("POST /auth/org/invites", "email,role_id")]
    [InlineData("PUT /auth/org/members/{user_id}/role", "role_id")]
    [InlineData("POST /auth/org/roles", "name,permissions")]
    [InlineData("PUT /auth/org/roles/{id}", "name,permissions")]
    public async Task Each_endpoint_that_reads_a_body_describes_it_in_snake_case(string endpoint, string properties)   // criterion 24
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        var document = await DescriptionAsync(factory);

        var operation = Operations(document)[endpoint];

        Assert.Equal(properties.Split(','), BodyProperties(document, operation));
    }

    [Fact]
    public async Task The_responses_are_described_in_snake_case()   // criterion 24
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        var schemas = (await DescriptionAsync(factory))["components"]!["schemas"]!.AsObject();

        string[] Properties(string schema) => [.. schemas[schema]!["properties"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal)];

        Assert.Equal(["email", "org_id", "org_name", "permissions", "roles", "sub"], Properties("MeResponse"));
        Assert.Equal(["email", "joined_at", "role", "user_id"], Properties("MemberItem"));
        Assert.Equal(["email", "expires_at", "id", "invited_at", "role"], Properties("InviteItem"));
        Assert.Equal(["id", "members", "name", "permissions"], Properties("RoleItem"));
        Assert.Equal(["catalog", "roles"], Properties("RolesResponse"));
        Assert.Equal(["email", "org_name", "role"], Properties("InvitePreviewResponse"));
        Assert.Equal(["access_token", "status"], Properties("LoginResponse"));
        Assert.Equal(["error", "retry_after_seconds"], Properties("TooManyAttemptsBody"));
        Assert.Equal(["error", "rules"], Properties("WeakPasswordBody"));
        Assert.Equal(["error"], Properties("ErrorBody"));
    }

    [Fact]
    public async Task The_company_endpoints_ask_for_a_bearer_token_and_the_public_ones_do_not()   // criterion 24
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        var document = await DescriptionAsync(factory);

        var scheme = document["components"]!["securitySchemes"]!["Bearer"]!;
        Assert.Equal("http", scheme["type"]!.GetValue<string>());
        Assert.Equal("bearer", scheme["scheme"]!.GetValue<string>());
        Assert.Equal("JWT", scheme["bearerFormat"]!.GetValue<string>());
        foreach (var (endpoint, operation) in Operations(document))
        {
            var guarded = endpoint.Split(' ')[1].StartsWith("/auth/org", StringComparison.Ordinal) || endpoint.EndsWith("/auth/me", StringComparison.Ordinal);
            Assert.Equal(guarded, operation["security"] is JsonArray { Count: > 0 });
        }
    }

    [Fact]
    public async Task Refresh_and_logout_take_the_refresh_cookie_and_nothing_else_does()   // criterion 24: the request of the two
    {
        await using var factory = new AuthAppFactory(postgres, keys);

        foreach (var (endpoint, operation) in Operations(await DescriptionAsync(factory)))
        {
            var cookies = (operation["parameters"] as JsonArray ?? []).Where(p => p!["in"]!.GetValue<string>() == "cookie").ToList();
            if (endpoint is "POST /auth/refresh" or "POST /auth/logout")
            {
                var cookie = Assert.Single(cookies)!;
                Assert.Equal("auth_rt", cookie["name"]!.GetValue<string>());
                Assert.Equal("string", cookie["schema"]!["type"]!.GetValue<string>());
                Assert.Null(operation["requestBody"]);
            }
            else
            {
                Assert.Empty(cookies);
            }
        }
    }

    [Theory]
    [InlineData("POST /auth/login", "200")]
    [InlineData("POST /auth/refresh", "200")]
    [InlineData("POST /auth/logout", "204")]
    public async Task The_answers_that_set_or_clear_the_refresh_cookie_say_so(string endpoint, string status)   // criterion 24
    {
        await using var factory = new AuthAppFactory(postgres, keys);

        var response = Operations(await DescriptionAsync(factory))[endpoint]["responses"]![status]!;

        Assert.Contains("auth_rt", response["headers"]!["Set-Cookie"]!["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task No_other_answer_says_it_sets_a_cookie()   // criterion 24: spec 0005's answers never set one
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        string[] setting = ["POST /auth/login 200", "POST /auth/refresh 200", "POST /auth/logout 204"];

        foreach (var (endpoint, operation) in Operations(await DescriptionAsync(factory)))
        {
            foreach (var (status, response) in operation["responses"]!.AsObject())
            {
                Assert.Equal(setting.Contains($"{endpoint} {status}"), response!["headers"]?["Set-Cookie"] is not null);
            }
        }
    }

    [Fact]
    public async Task Every_429_carries_retry_after()   // criterion 24; spec 0008: on every operation
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        var limited = 0;

        foreach (var (_, operation) in Operations(await DescriptionAsync(factory)))
        {
            if (operation["responses"]!["429"] is { } response)
            {
                var header = response["headers"]!["Retry-After"]!;
                Assert.Equal("integer", header["schema"]!["type"]!.GetValue<string>());
                Assert.Contains("retry_after_seconds", header["description"]!.GetValue<string>());
                limited++;
            }
        }

        Assert.Equal(Endpoints.Length, limited);   // every operation can say 429 now (spec 0008)
    }

    [Fact]
    public async Task The_description_names_no_server_taken_from_the_request()   // a Host header must not reach the document
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, DocumentUrl);
        request.Headers.Host = "evil.example";

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("evil.example", text, StringComparison.OrdinalIgnoreCase);
        Assert.Null(JsonNode.Parse(text)!["servers"]);
    }

    [Fact]
    public async Task The_reset_and_accept_answers_name_the_password_rules()
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        var operations = Operations(await DescriptionAsync(factory));

        foreach (var endpoint in new[] { "POST /auth/password/reset", "POST /auth/invites/accept" })
        {
            var schema = operations[endpoint]["responses"]!["400"]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>();
            Assert.EndsWith("WeakPasswordBody", schema);
        }
    }

    [Fact]
    public async Task The_interactive_reference_is_served_in_development_only()   // criterion 24
    {
        await using var development = new AuthAppFactory(postgres, keys);
        using var developmentClient = development.CreateClient();
        using var shown = await developmentClient.GetAsync("/auth/scalar");
        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        Assert.Equal("text/html", shown.Content.Headers.ContentType?.MediaType);

        await using var production = new AuthAppFactory(postgres, keys).WithEnvironment("Production");
        using var productionClient = production.CreateClient();
        using var hidden = await productionClient.GetAsync("/auth/scalar");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using var alsoHidden = await productionClient.GetAsync("/auth/scalar/v1");
        Assert.Equal(HttpStatusCode.NotFound, alsoHidden.StatusCode);
    }
}
