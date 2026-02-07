using System.Diagnostics.CodeAnalysis;
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
    /// </summary>
    public static bool IsInvitable(string email)
    {
        ArgumentNullException.ThrowIfNull(email);

        var at = email.LastIndexOf('@');
        if (at < 0 || MayStandForAnotherAddress(email))
        {
            return false;
        }

        var labels = email[(at + 1)..].Split('.');
        return labels.Length > 1
            && labels.All(label => label.Length > 0)
            && !labels[^1].All(char.IsAsciiDigit)
            && !email[(at + 1)..].StartsWith('[');
    }

    /// <summary>
    /// Whether the address holds a character outside ASCII that the account lookup may fold into an ASCII one, so that
    /// the address may stand for another: <c>ſ</c> (U+017F) whose upper case is <c>S</c>, the Kelvin sign (U+212A) whose
    /// lower case and canonical form are <c>K</c>, <c>İ</c> (U+0130) and <c>ı</c> (U+0131).
    /// <para>
    /// This is how an account would be taken over. Accepting an invitation finds an existing account by the normalised
    /// address and gives it the password the person chose. On a host with ICU the normaliser upper-cases
    /// <c>ſteve@corp.test</c> to the normalised address of <c>steve@corp.test</c>, while the link goes to the mailbox of
    /// <c>ſteve@corp.test</c>: a manager who can read that mailbox would set the password of steve's account. So such an
    /// address is never invited, and an invitation stored for one is never accepted.
    /// </para>
    /// <para>
    /// The four are named, and not only found through the casing tables, because .NET folds <c>ſ</c> only where ICU is
    /// present and folds neither <c>İ</c> nor <c>ı</c> at all, while other software on the way of a mail may; the rule
    /// must be the same on every host. No string normalisation is used: the container runs with invariant globalisation.
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
