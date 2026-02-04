using Auth.Server.Tenancy;

namespace Auth.Server.Api;

public static class TenancyEndpoints
{
    /// <summary>
    /// The endpoints of spec 0005: the caller's own data and the company API. Everything under <c>/auth/org</c> acts on
    /// the caller's company, taken from the database, and needs an access token; the invitation endpoints that need
    /// none are mapped beside it.
    /// </summary>
    public static IEndpointRouteBuilder MapTenancyApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(OrgEndpoints.MePath, OrgEndpoints.MeAsync)
            .RequireAuthorization()
            .Produces<MeResponse>()
            .ProducesUnauthorized()
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionsChanged, TenancyErrors.Forbidden);

        var company = app.MapGroup(OrgEndpoints.OrgPath).RequireAuthorization();

        company.MapGet("", OrgEndpoints.GetAsync)
            .Produces<OrgResponse>()
            .ProducesUnauthorized()
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionsChanged, TenancyErrors.Forbidden);
        company.MapPatch("", OrgEndpoints.RenameAsync)
            .ReadsJson<RenameOrgRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesUnauthorized()
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionsChanged, TenancyErrors.Forbidden);

        return app;
    }
}
