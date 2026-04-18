using System.Buffers;
using System.Text.Json;
using Microsoft.Net.Http.Headers;

namespace Auth.Server.Requests;

/// <summary>
/// Reads the request bodies of the API: a JSON object of at most <see cref="MaxBytes"/> whose named properties are
/// non-blank strings. Shared by login and the account endpoints, so that "malformed" means the same everywhere.
/// </summary>
public static class JsonObjectBody
{
    /// <summary>Largest accepted request body, in bytes.</summary>
    public const int MaxBytes = 8 * 1024;

    internal const string JsonMediaType = "application/json";

    public static bool IsJson(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var parsed)
        && string.Equals(parsed.MediaType.Value, JsonMediaType, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the whole body, or returns <see langword="null"/> as soon as it exceeds <paramref name="limit"/> bytes.</summary>
    public static async Task<byte[]?> ReadBoundedAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var buffer = ArrayPool<byte>.Shared.Rent(limit + 1);
        try
        {
            var total = 0;
            while (total <= limit)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total, limit + 1 - total), cancellationToken);
                if (read == 0)
                {
                    return buffer.AsSpan(0, total).ToArray();
                }

                total += read;
            }

            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static bool TryGetRequiredString(JsonElement obj, string name, out string value)
    {
        value = string.Empty;
        if (!obj.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = element.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text;
        return true;
    }

    /// <summary>
    /// The body as a JSON object whose members the caller reads one by one (<see cref="JsonFields"/>), or <see langword="null"/> when it is not
    /// acceptable: wrong content type, too large, not JSON, not a JSON object, or a member whose name holds an unpaired surrogate escape
    /// (a name that no lookup of a member could survive). This is the one place that judges a body, for every reader.
    /// </summary>
    public static async Task<JsonFields?> ReadObjectAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsJson(request.ContentType) || request.ContentLength > MaxBytes)
        {
            return null;
        }

        var body = await ReadBoundedAsync(request.Body, MaxBytes, cancellationToken);
        if (body is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var member in root.EnumerateObject())
            {
                _ = member.Name;   // throws on a name that holds an unpaired surrogate escape
            }

            return new JsonFields(root.Clone());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The values of the properties <paramref name="names"/>, in that order, or <see langword="null"/> when the
    /// request is not acceptable: wrong content type, too large, not JSON, not a JSON object, a member whose name holds an
    /// unpaired surrogate escape (wherever it stands in the body), or a named property that is missing, not a string, or blank.
    /// </summary>
    public static async Task<string[]?> ReadStringsAsync(HttpRequest request, string[] names, CancellationToken cancellationToken)
    {
        var values = await ReadStringsAsync(request, names, [], cancellationToken);
        return values is null ? null : Array.ConvertAll(values, value => value ?? string.Empty);   // none of the named ones is missing
    }

    /// <summary>
    /// Like the overload above, with <paramref name="optional"/> members after the <paramref name="required"/> ones: an optional member may
    /// be missing (its value is <see langword="null"/>); when it is there it is held to the rule of every other member (a non-blank
    /// string), otherwise the request is not acceptable.
    /// </summary>
    public static async Task<string?[]?> ReadStringsAsync(
        HttpRequest request, string[] required, string[] optional, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(required);
        ArgumentNullException.ThrowIfNull(optional);

        if (await ReadObjectAsync(request, cancellationToken) is not { } fields)
        {
            return null;
        }

        var values = new string?[required.Length + optional.Length];
        for (var i = 0; i < required.Length; i++)
        {
            if (!fields.TryString(required[i], out var value))
            {
                return null;
            }

            values[i] = value;
        }

        for (var i = 0; i < optional.Length; i++)
        {
            if (!fields.TryOptionalString(optional[i], out values[required.Length + i]))
            {
                return null;
            }
        }

        return values;
    }
}
