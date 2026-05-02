using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace Auth.FakeOidc;

/// <summary>
/// Who signs in at the fake, and how: the fake's own query parameters on its authorization endpoint. <c>person</c> is one of
/// <see cref="FakePeople"/>; the others are optional: <c>email</c>, <c>verified</c> (<c>true</c> or <c>false</c>), <c>subject</c> (Google's
/// <c>sub</c> or Microsoft's <c>oid</c>), <c>tenant</c> (Microsoft's <c>tid</c>), <c>edov</c> (Microsoft's <c>xms_edov</c>: <c>true</c> and
/// <c>false</c> become JSON booleans, anything else a string) and <c>defect</c> (<see cref="FakeDefects"/>). The page builds them; a script or a
/// test writes them.
/// </summary>
public sealed record FakeSignIn(
    string Person, string? Email = null, bool? Verified = null, string? Subject = null, string? Tenant = null, string? Edov = null,
    string? Defect = null)
{
    /// <summary>The authorization address (absolute, or a path with its query) with these parameters added.</summary>
    public string AppendTo(string authorizationUrl)
    {
        ArgumentNullException.ThrowIfNull(authorizationUrl);

        var parameters = new List<KeyValuePair<string, string?>> { new("person", Person) };
        Add(parameters, "email", Email);
        Add(parameters, "verified", Verified switch { true => "true", false => "false", null => null });
        Add(parameters, "subject", Subject);
        Add(parameters, "tenant", Tenant);
        Add(parameters, "edov", Edov);
        Add(parameters, "defect", Defect);
        return QueryHelpers.AddQueryString(authorizationUrl, parameters);
    }

    /// <summary>The sign-in a request names; <see langword="null"/> when it names no person (the page is shown then).</summary>
    public static FakeSignIn? From(IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (One(query, "person") is not { } person)
        {
            return null;
        }

        var verified = One(query, "verified");
        return new FakeSignIn(
            person,
            One(query, "email"),
            verified is null ? null : string.Equals(verified, "true", StringComparison.Ordinal),
            One(query, "subject"),
            One(query, "tenant"),
            One(query, "edov"),
            One(query, "defect"));
    }

    private static void Add(List<KeyValuePair<string, string?>> parameters, string name, string? value)
    {
        if (value is not null)
        {
            parameters.Add(new(name, value));
        }
    }

    private static string? One(IQueryCollection query, string name) =>
        query.TryGetValue(name, out var values) && values.Count == 1 && !string.IsNullOrEmpty(values[0]) ? values[0] : null;
}
