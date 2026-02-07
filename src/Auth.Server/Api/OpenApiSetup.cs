using System.Net.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace Auth.Server.Api;

/// <summary>
/// The OpenAPI description of the service (spec 0005 → OpenAPI): every endpoint of specs 0001–0005 with its request, its
/// responses and the error codes each response carries, at <c>GET /auth/openapi/v1.json</c> in every environment. An
/// interactive reference of it is served in Development only. The endpoints describe themselves where they are mapped
/// (<see cref="EndpointMetadata"/>); this adds what the framework cannot see: the JSON bodies the handlers read, the error
/// codes, the bearer scheme, and the two endpoints that are not minimal-API routes.
/// </summary>
public static class OpenApiSetup
{
    public const string DocumentName = "v1";
    public const string DocumentPath = "/auth/openapi/{documentName}.json";
    public const string ReferencePath = "/auth/scalar";
    public const string BearerScheme = "Bearer";
    public const string JwksPath = "/auth/.well-known/jwks.json";

    public static IServiceCollection AddAuthOpenApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddOpenApi(DocumentName, options =>
        {
            options.AddDocumentTransformer(DescribeDocumentAsync);
            options.AddOperationTransformer(DescribeOperationAsync);
        });
    }

    public static IEndpointRouteBuilder MapAuthOpenApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapOpenApi(DocumentPath);
        if (app.Environment.IsDevelopment())
        {
            app.MapScalarApiReference(ReferencePath, options => options.WithOpenApiRoutePattern(DocumentPath));
        }

        return app;
    }

    private static Task DescribeDocumentAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "Auth-Core",
            Version = DocumentName,
            Description = "Accounts, sessions, companies, members, roles and invitations. Errors are `{\"error\":\"<code>\"}`; "
                + "every response of the account and company endpoints is marked `Cache-Control: no-store`.",
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "The access token of `POST /auth/login` or `POST /auth/refresh`. The company API reads the caller's "
                + "membership and permissions from the database, not from the token.",
        };

        // Two endpoints that are not routes of ours: the key set is OpenIddict's, the health check has no metadata.
        document.Paths ??= [];
        document.Paths[JwksPath] = PathWithGet(
            "The public keys that verify the access tokens (RFC 7517).", "200", "The JSON Web Key Set.", "application/json");
        document.Paths[AccountEndpoints.HealthPath] = PathWithGet(
            "Whether the service works. `Degraded` means the product's manifest file was not used and the last valid one is active.",
            "200", "`Healthy` or `Degraded`.", "text/plain");
        return Task.CompletedTask;
    }

    private static OpenApiPathItem PathWithGet(string summary, string status, string description, string contentType) => new()
    {
        Operations = new Dictionary<HttpMethod, OpenApiOperation>
        {
            [HttpMethod.Get] = new OpenApiOperation
            {
                Summary = summary,
                Responses = new OpenApiResponses
                {
                    [status] = new OpenApiResponse
                    {
                        Description = description,
                        Content = new Dictionary<string, OpenApiMediaType> { [contentType] = new OpenApiMediaType() },
                    },
                },
            },
        },
    };

    private static async Task DescribeOperationAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;

        // The body the handler reads itself.
        if (metadata.OfType<JsonRequestMetadata>().FirstOrDefault() is { } request)
        {
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new OpenApiMediaType { Schema = await context.GetOrCreateSchemaAsync(request.Body, null, cancellationToken) },
                },
            };
        }

        // The codes of each error status, from every place that declared the status.
        foreach (var status in metadata.OfType<ErrorCodesMetadata>().GroupBy(e => e.Status))
        {
            var codes = status.SelectMany(e => e.Codes).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            if (operation.Responses?.TryGetValue(status.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), out var response) == true && response is OpenApiResponse concrete)
            {
                concrete.Description = "Error: " + string.Join(", ", codes);
            }
        }

        if (metadata.OfType<IAuthorizeData>().Any())
        {
            operation.Security =
            [
                new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(BearerScheme, null)] = [] },
            ];
        }
    }
}
