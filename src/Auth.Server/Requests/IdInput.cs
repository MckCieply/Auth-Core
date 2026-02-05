namespace Auth.Server.Requests;

/// <summary>An id in a path or a body is a UUID in its usual form, 8-4-4-4-12 hexadecimal digits, and nothing else.</summary>
public static class IdInput
{
    public static bool TryParse(string? text, out Guid id) => Guid.TryParseExact(text, "D", out id);
}
