namespace Auth.Server.Requests;

/// <summary>An id in a path or a body is a UUID in its usual form, 8-4-4-4-12 hexadecimal digits, and nothing else.</summary>
public static class IdInput
{
    private const int Length = 36;

    /// <summary>
    /// Parses the id. The length and the white space are checked first: the framework's parser trims white space before it
    /// reads the "D" form, so <c>"&lt;id&gt; "</c> would otherwise pass.
    /// </summary>
    public static bool TryParse(string? text, out Guid id)
    {
        if (text is not { Length: Length } || text.Any(char.IsWhiteSpace))
        {
            id = Guid.Empty;
            return false;
        }

        return Guid.TryParseExact(text, "D", out id);
    }
}
