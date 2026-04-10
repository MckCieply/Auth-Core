using Auth.Infrastructure.Persistence;

namespace Auth.Server.Tenancy;

/// <summary>The kind of a space as the access token and <c>GET /auth/me</c> name it (spec 0010 → Access token).</summary>
public static class SpaceKinds
{
    public const string OrganizationName = "organization";
    public const string PersonalName = "personal";

    public static string NameOf(CompanyKind kind) => kind == CompanyKind.Personal ? PersonalName : OrganizationName;
}
