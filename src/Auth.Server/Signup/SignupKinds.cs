using Auth.Infrastructure.Persistence;
using Auth.Server.Requests;

namespace Auth.Server.Signup;

/// <summary>The kind of a sign-up as the API and the audit log name it (spec 0010): <c>organization</c> or <c>private</c>.</summary>
public static class SignupKinds
{
    public const string OrganizationName = "organization";
    public const string PrivateName = "private";

    private const string Field = "kind";

    public static string NameOf(SignupKind kind) => kind == SignupKind.Private ? PrivateName : OrganizationName;

    /// <summary>Exactly one of the two names. Anything else is not a kind.</summary>
    public static bool TryParse(string? name, out SignupKind kind)
    {
        if (string.Equals(name, PrivateName, StringComparison.Ordinal))
        {
            kind = SignupKind.Private;
            return true;
        }

        kind = SignupKind.Organization;
        return string.Equals(name, OrganizationName, StringComparison.Ordinal);
    }

    /// <summary>The member <c>kind</c> of a request: absent is an organization; otherwise one of the two names, as a string.</summary>
    public static bool TryRead(JsonFields body, out SignupKind kind)
    {
        kind = SignupKind.Organization;
        if (!body.Has(Field))
        {
            return true;
        }

        return body.TryString(Field, out var name) && TryParse(name, out kind);
    }
}
