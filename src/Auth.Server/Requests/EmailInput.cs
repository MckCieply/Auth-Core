using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Identity;

namespace Auth.Server.Requests;

/// <summary>
/// Decides whether a submitted email can be used at all, before anything is counted or looked up. The rule is our
/// own, so that it is the same on every host: the framework's normaliser rejects U+FFFE where ICU is present and
/// accepts it where it is not (spec 0004, criterion 16).
/// </summary>
public static class EmailInput
{
    /// <summary>The longest address the mail endpoints accept (RFC 5321). Login does not apply it.</summary>
    public const int MaxLength = 254;

    /// <summary>
    /// <see langword="false"/> for an email holding a control character, a Unicode noncharacter or an unpaired
    /// surrogate.
    /// </summary>
    public static bool IsWellFormed(string email)
    {
        ArgumentNullException.ThrowIfNull(email);

        for (var i = 0; i < email.Length; i++)
        {
            var c = email[i];
            if (char.IsControl(c))
            {
                return false;
            }

            if (char.IsHighSurrogate(c) && i + 1 < email.Length && char.IsLowSurrogate(email[i + 1]))
            {
                // U+xFFFE and U+xFFFF are noncharacters in every plane.
                if ((char.ConvertToUtf32(c, email[i + 1]) & 0xFFFE) == 0xFFFE)
                {
                    return false;
                }

                i++;
                continue;
            }

            if (char.IsSurrogate(c) || c is (>= '\uFDD0' and <= '\uFDEF') or '\uFFFE' or '\uFFFF')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The email as the account lookup normalises it, or <see langword="false"/> when it is not well formed or the
    /// host's normaliser rejects it.
    /// </summary>
    public static bool TryNormalize(string email, ILookupNormalizer normalizer, [NotNullWhen(true)] out string? normalized)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(normalizer);

        normalized = null;
        if (!IsWellFormed(email))
        {
            return false;
        }

        try
        {
            normalized = normalizer.NormalizeEmail(email);
        }
        catch (ArgumentException)
        {
            // Whatever else this host's normaliser refuses: a malformed request, not a server error.
            return false;
        }

        return normalized is not null;
    }
}
