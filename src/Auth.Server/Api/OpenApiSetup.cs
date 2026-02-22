using System.Globalization;
using System.Net.Http;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace Auth.Server.Api;

/// <summary>
/// The OpenAPI description of the service (spec 0005 → OpenAPI): every endpoint of specs 0001–0008 with its request, its
/// responses and the error codes each response carries (and the 429 and 415 that the pipeline adds to every endpoint that can say them), at <c>GET /auth/openapi/v1.json</c> in every environment. An
/// interactive reference of it is served in Development only. The endpoints describe themselves where they are mapped
/// (<see cref="EndpointMetadata"/>); this adds what the framework cannot see: the JSON bodies and the cookie the handlers
/// read, the headers they set, the error codes, the bearer scheme, and the two endpoints that are not minimal-API routes.
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
            // The bundle is served from the same origin. The one inline script carries a nonce (SecurityHeaders puts it into the
            // policy); the default fonts, the telemetry and the agent would reach other hosts, which the policy forbids.
            app.MapScalarApiReference(
                ReferencePath,
                options => options.WithOpenApiRoutePattern(DocumentPath).WithNonce().DisableDefaultFonts().DisableTelemetry().DisableAgent());
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
                + "every response of the account and company endpoints is marked `Cache-Control: no-store`, and every response carries the "
                + "security headers. A request over the per-address limit is answered `429 too_many_requests` with `Retry-After`, on every "
                + "endpoint. The company API is guarded by the built-in permissions `members:manage`, `roles:manage`, `org:manage` and "
                + "`org:delete`.",
        };

        // No server: the framework would name the one of the request, from its Host header (AllowedHosts is `*`), and a
        // cached copy could point generated clients, and their tokens, at another host. Without one, a client resolves the
        // paths against where it fetched the document.
        document.Servers = [];

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

        // Two endpoints that are not routes of ours: the key set is OpenIddict's, the health check has no metadata. Like every
        // endpoint under /auth/ they can say 429.
        document.Paths ??= [];
        document.Paths[JwksPath] = WithTooManyRequests(PathWithGet(
            "The public keys that verify the access tokens (RFC 7517).", "200", "The JSON Web Key Set.", "application/json"));
        document.Paths[AccountEndpoints.HealthPath] = WithTooManyRequests(PathWithGet(
            "Whether the service works. `Degraded` means the product's manifest file was not used and the last valid one is active. "
            + "It does not reach the database. Answers `GET` and `HEAD`.",
            "200", "`Healthy` or `Degraded`.", "text/plain"));
        return Task.CompletedTask;
    }

    private static OpenApiPathItem WithTooManyRequests(OpenApiPathItem item)
    {
        foreach (var operation in item.Operations!.Values)
        {
            var response = ErrorResponse(
                new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    Properties = new Dictionary<string, IOpenApiSchema>
                    {
                        ["error"] = new OpenApiSchema { Type = JsonSchemaType.String },
                        ["retry_after_seconds"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
                    },
                });
            response.Description = "Error: " + TenancyErrors.TooManyRequests;
            AddRetryAfter(response);
            operation.Responses!["429"] = response;
        }

        return item;
    }

    private static OpenApiResponse ErrorResponse(IOpenApiSchema schema) => new()
    {
        Description = "Error",
        Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new OpenApiMediaType { Schema = schema } },
    };

    private static void AddRetryAfter(OpenApiResponse response)
    {
        response.Headers ??= new Dictionary<string, IOpenApiHeader>();
        response.Headers["Retry-After"] = new OpenApiHeader
        {
            Description = EndpointMetadata.RetryAfterDescription,
            Schema = new OpenApiSchema { Type = JsonSchemaType.Integer },
        };
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

        // The cookie the handler reads itself: refresh and logout take no body.
        foreach (var cookie in metadata.OfType<CookieRequestMetadata>())
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = cookie.Name,
                In = ParameterLocation.Cookie,
                Description = cookie.Description,
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            });
        }

        // The codes of each error status, from every place that declared the status, and two answers that come from the pipeline and
        // not from a handler (spec 0008): every endpoint can say 429 too_many_requests, and every one that reads a JSON body can
        // say 415 unsupported_media_type. A status that no handler declared gets its response made here.
        var codesByStatus = metadata.OfType<ErrorCodesMetadata>()
            .GroupBy(e => e.Status)
            .ToDictionary(g => g.Key, g => g.SelectMany(e => e.Codes).ToHashSet(StringComparer.Ordinal));
        AddCode(codesByStatus, StatusCodes.Status429TooManyRequests, TenancyErrors.TooManyRequests);
        if (metadata.OfType<JsonRequestMetadata>().Any())
        {
            AddCode(codesByStatus, StatusCodes.Status415UnsupportedMediaType, TenancyErrors.UnsupportedMediaType);
        }

        operation.Responses ??= [];
        foreach (var (status, codes) in codesByStatus)
        {
            var key = status.ToString(CultureInfo.InvariantCulture);
            if (!(operation.Responses.TryGetValue(key, out var existing) && existing is OpenApiResponse response))
            {
                var schema = await context.GetOrCreateSchemaAsync(
                    status == StatusCodes.Status429TooManyRequests ? typeof(TooManyAttemptsBody) : typeof(ErrorBody), null, cancellationToken);
                response = ErrorResponse(schema);
                operation.Responses[key] = response;
            }

            response.Description = "Error: " + string.Join(", ", codes.Order(StringComparer.Ordinal));
            if (status == StatusCodes.Status429TooManyRequests)
            {
                AddRetryAfter(response);
            }
        }

        // The headers the handler sets on a response: the refresh cookie, and the Retry-After of a 429 or a 503.
        foreach (var header in metadata.OfType<ResponseHeaderMetadata>())
        {
            if (operation.Responses.TryGetValue(header.Status.ToString(CultureInfo.InvariantCulture), out var response) && response is OpenApiResponse concrete)
            {
                concrete.Headers ??= new Dictionary<string, IOpenApiHeader>();
                concrete.Headers[header.Name] = new OpenApiHeader
                {
                    Description = header.Description,
                    Schema = new OpenApiSchema { Type = header.Type },
                };
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

    private static void AddCode(Dictionary<int, HashSet<string>> codesByStatus, int status, string code)
    {
        if (!codesByStatus.TryGetValue(status, out var codes))
        {
            codesByStatus[status] = codes = new HashSet<string>(StringComparer.Ordinal);
        }

        codes.Add(code);
    }
}
