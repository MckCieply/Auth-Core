using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Identity;
using MimeKit;

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
    /// Whether the text is one mailbox and nothing more: <c>local@domain</c>, no name, no list, no comment. An invitation
    /// creates an account with the address and mails it, so a typo such as <c>bob.acme.test</c> is refused at once instead
    /// of becoming an invitation that never arrives.
    /// </summary>
    public static bool IsMailbox(string email)
    {
        ArgumentNullException.ThrowIfNull(email);

        var at = email.LastIndexOf('@');
        return at > 0
            && at < email.Length - 1
            && MailboxAddress.TryParse(email, out var mailbox)
            && string.Equals(mailbox.Address, email, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether an invitation may be mailed to the address (spec 0005 → Invitations), on top of <see cref="IsMailbox"/>. Two
    /// kinds of single mailbox are still refused:
    /// <list type="bullet">
    /// <item>one that <see cref="MayStandForAnotherAddress">may stand for another address</see>;</item>
    /// <item>one whose domain is a literal (<c>joe@[10.0.0.5]</c>), has no dot (<c>joe@localhost</c>, <c>joe@intranet</c>)
    /// or ends in a part of digits only (<c>joe@10.0.0.5</c>). Here a member, not an account, chooses where a mail goes,
    /// and a manager must not make the instance's mail relay deliver to an internal host.</item>
    /// </list>
    /// The domain rule must hold for the domain the relay is given, not only for the one typed. The transport hands the
    /// relay MimeKit's IDN-encoded form of the address or, to a relay that takes UTF-8 addresses, the address as typed,
    /// which the relay then maps itself. IDNA drops some characters and maps others (<c>intranet.</c> plus a soft hyphen
    /// becomes <c>intranet.</c>; a fullwidth 5 becomes 5), and how much of that is done depends on the host's IDN tables.
    /// So the rule is applied to the typed domain and to MimeKit's encoded one, and a domain holding a character outside
    /// ASCII that IDNA could drop or turn into a digit or a dot (<see cref="KeepsItsShapeThroughIdna"/>) is refused.
    /// </summary>
    public static bool IsInvitable(string email)
    {
        ArgumentNullException.ThrowIfNull(email);

        var at = email.LastIndexOf('@');
        if (at < 0 || MayStandForAnotherAddress(email))
        {
            return false;
        }

        var domain = email[(at + 1)..];
        return IsDomainName(domain, encoded: false)
            && KeepsItsShapeThroughIdna(domain)
            && EncodedDomainOf(email) is { } sent
            && IsDomainName(sent, encoded: true);
    }

    /// <summary>
    /// A name with a dot between non-empty labels whose last label can be the end of a real domain name (see
    /// <see cref="IsTopLevelLabel"/>); not a literal. Numbers, hexadecimal forms (<c>127.0x1</c>) and names such as <c>host.123</c>
    /// are not domain names: a resolver may turn them into an address.
    /// </summary>
    private static bool IsDomainName(string domain, bool encoded)
    {
        var labels = domain.Split('.');
        return labels.Length > 1
            && labels.All(label => label.Length > 0)
            && IsTopLevelLabel(labels[^1], encoded)
            && !domain.StartsWith('[');
    }

    /// <summary>
    /// The last label of a domain: <c>xn--</c> and more, or two or more letters. As the relay gets it (<paramref name="encoded"/>)
    /// the letters are ASCII. As typed they are letters of any script, with their combining marks, so that
    /// <c>x@пример.рф</c> still passes. Decided by the characters alone, so that the host's IDN tables and its globalisation
    /// mode change nothing.
    /// </summary>
    private static bool IsTopLevelLabel(string label, bool encoded)
    {
        if (label.StartsWith("xn--", StringComparison.OrdinalIgnoreCase))
        {
            return label.Length > 4 && label[4..].All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
        }

        if (encoded)
        {
            return label.Length >= 2 && label.All(char.IsAsciiLetter);
        }

        var runes = label.EnumerateRunes().ToList();
        return runes.Count(Rune.IsLetter) >= 2
            && runes.All(rune => Rune.IsLetter(rune)
                || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark);
    }

    /// <summary>
    /// Whether every character of the domain outside ASCII is a letter or a combining mark that IDNA keeps. IDNA maps a
    /// letter to letters, but it drops format characters and variation selectors, and maps fullwidth forms, other digits
    /// and other dots to ASCII digits and dots: such a character could turn the domain into one the rule refuses.
    /// </summary>
    private static bool KeepsItsShapeThroughIdna(string domain)
    {
        foreach (var rune in domain.EnumerateRunes())
        {
            if (rune.IsAscii)
            {
                continue;
            }

            var value = rune.Value;
            var ignored = value is 0x034F or (>= 0x180B and <= 0x180F) or (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0100 and <= 0xE01EF);
            var widthForm = value is >= 0xFF00 and <= 0xFFEF;
            var kept = Rune.GetUnicodeCategory(rune) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
                or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
            if (ignored || widthForm || !kept)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The domain of MimeKit's IDN-encoded form of the address; <see langword="null"/> when there is none.</summary>
    private static string? EncodedDomainOf(string email)
    {
        if (!MailboxAddress.TryParse(email, out var mailbox))
        {
            return null;
        }

        try
        {
            var encoded = mailbox.GetAddress(idnEncode: true);
            return encoded[(encoded.LastIndexOf('@') + 1)..];
        }
        catch (ArgumentException)
        {
            // A domain IDNA cannot encode cannot be mailed either.
            return null;
        }
    }

    /// <summary>
    /// Whether an account's stored address is the invited address itself: equal character for character, except that the
    /// ASCII letters A–Z and a–z match regardless of case. Nothing else is folded.
    /// <para>
    /// The account lookup matches by the normalised address (NFC, then <see cref="string.ToUpperInvariant()"/>), and that
    /// folds different addresses together: the micro sign and Greek mu, final and plain sigma, a decomposed and a composed
    /// letter, and more on hosts with ICU. A link proves only the mailbox it was sent to, so accepting an invitation may
    /// act on an existing account only when this holds; otherwise one address could set the password of another's account.
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> would not do: it folds through the same case tables (µ equals μ).
    /// </para>
    /// </summary>
    public static bool IsSameAddress(string? stored, string invited)
    {
        ArgumentNullException.ThrowIfNull(invited);

        if (stored is null || stored.Length != invited.Length)
        {
            return false;
        }

        for (var i = 0; i < stored.Length; i++)
        {
            if (AsciiLower(stored[i]) != AsciiLower(invited[i]))
            {
                return false;
            }
        }

        return true;

        static char AsciiLower(char c) => c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c;
    }

    /// <summary>
    /// Whether the address holds a character outside ASCII whose case is an ASCII letter, so that it reads as an ASCII
    /// address: <c>ſ</c> (U+017F) whose upper case is <c>S</c>, the Kelvin sign (U+212A) whose lower case is <c>k</c>,
    /// and <c>İ</c> (U+0130) and <c>ı</c> (U+0131), which .NET does not fold but other software may.
    /// <para>
    /// This is defence in depth, not the protection against taking over an account: that is <see cref="IsSameAddress"/>,
    /// at acceptance. A list like this one cannot be complete (the normaliser also folds µ into μ, ς into σ, a decomposed
    /// letter into a composed one…); it only keeps an invitation to the most obvious look-alikes of ASCII addresses from
    /// being sent at all. The four are named, and not only found through the casing tables, because .NET folds <c>ſ</c>
    /// only where ICU is present, and the rule must be the same on every host. No string normalisation is used: the
    /// container runs with invariant globalisation.
    /// </para>
    /// </summary>
    public static bool MayStandForAnotherAddress(string email)
    {
        ArgumentNullException.ThrowIfNull(email);

        return email.Any(c => !char.IsAscii(c)
            && (c is '\u017F' or '\u212A' or '\u0130' or '\u0131'
                || char.IsAscii(char.ToUpperInvariant(c))
                || char.IsAscii(char.ToLowerInvariant(c))));
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
