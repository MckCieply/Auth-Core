using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Auth.Server.Requests;

/// <summary>What a JSON member that should be a boolean is (spec 0010): absent, <c>true</c>, <c>false</c>, or anything else (<c>null</c> included).</summary>
public enum JsonBoolean
{
    Absent,
    True,
    False,
    Invalid,
}

/// <summary>
/// The members of a request body read one by one (<see cref="JsonObjectBody.ReadObjectAsync"/>), for an endpoint whose checks of the members
/// are interleaved with other checks: spec 0010 judges the switch of a sign-up's kind before its address. A string follows the rule of
/// <see cref="JsonObjectBody"/>: present, a JSON string, not blank, and with no lone surrogate.
/// </summary>
public readonly record struct JsonFields(JsonElement Root)
{
    public bool Has(string name) => Root.TryGetProperty(name, out _);

    /// <summary>The member as a non-blank string; <see langword="false"/> when it is missing or anything else.</summary>
    public bool TryString(string name, [NotNullWhen(true)] out string? value)
    {
        value = null;
        try
        {
            if (!JsonObjectBody.TryGetRequiredString(Root, name, out var text))
            {
                return false;
            }

            value = text;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;   // a string holding an unpaired surrogate escape
        }
    }

    /// <summary>An optional string member: absent is fine (<paramref name="value"/> is <see langword="null"/>); present it must be a non-blank string.</summary>
    public bool TryOptionalString(string name, out string? value)
    {
        value = null;
        return !Has(name) || TryString(name, out value);
    }

    public JsonBoolean Boolean(string name) =>
        !Root.TryGetProperty(name, out var element)
            ? JsonBoolean.Absent
            : element.ValueKind switch
            {
                JsonValueKind.True => JsonBoolean.True,
                JsonValueKind.False => JsonBoolean.False,
                _ => JsonBoolean.Invalid,
            };
}
