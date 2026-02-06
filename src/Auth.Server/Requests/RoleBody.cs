using System.Text.Json;

namespace Auth.Server.Requests;

/// <summary>What a role is created or replaced with: a name and a list of permissions.</summary>
public sealed record RoleBody(string Name, IReadOnlyList<string> Permissions);

/// <summary>
/// Reads the body of <c>POST /auth/org/roles</c> and <c>PUT /auth/org/roles/{id}</c> (spec 0005 → General rules): a JSON
/// object of at most <see cref="JsonObjectBody.MaxBytes"/> bytes with a <c>name</c> that is a non-blank string and a
/// <c>permissions</c> that is an array of strings. Whether the name is acceptable and the permissions exist is for the
/// caller to say; this says only whether the body has that shape.
/// </summary>
public static class RoleBodyReader
{
    /// <summary>More than this many entries is not a list of permissions; the 8 KiB body would hold about as many.</summary>
    public const int MaxPermissions = 500;

    public static async Task<RoleBody?> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!JsonObjectBody.IsJson(request.ContentType) || request.ContentLength > JsonObjectBody.MaxBytes)
        {
            return null;
        }

        var body = await JsonObjectBody.ReadBoundedAsync(request.Body, JsonObjectBody.MaxBytes, cancellationToken);
        if (body is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !JsonObjectBody.TryGetRequiredString(root, "name", out var name)
                || !root.TryGetProperty("permissions", out var list)
                || list.ValueKind != JsonValueKind.Array
                || list.GetArrayLength() > MaxPermissions)
            {
                return null;
            }

            var permissions = new List<string>();
            foreach (var element in list.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                permissions.Add(element.GetString() ?? "");
            }

            return new RoleBody(name, permissions);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // InvalidOperationException: GetString() on a string holding an unpaired surrogate escape (e.g. "\ud800").
            return null;
        }
    }
}
