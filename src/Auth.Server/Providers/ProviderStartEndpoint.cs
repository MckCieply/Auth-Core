using Auth.Infrastructure.Persistence;
using Auth.Server.Api;
using Auth.Server.Email;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Client.AspNetCore;
using ClientProperties = OpenIddict.Client.AspNetCore.OpenIddictClientAspNetCoreConstants.Properties;

namespace Auth.Server.Providers;

/// <summary>
/// <c>GET /auth/providers/{name}/start</c> (spec 0011): the product's button is a link here. It decides what the round trip is for from the query,
/// reads no account and no invitation, and challenges OpenIddict's client, which answers <c>302</c> to the provider and sets its request-forgery
/// cookie. The purpose, the provider and the hash of an invitation token travel in the encrypted state.
/// </summary>
public static class ProviderStartEndpoint
{
    public const string InviteParameter = "invite";
    public const string PurposeParameter = "purpose";

    public static IResult Handle(HttpContext http, string name, ProviderCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(catalog);

        // OpenIddict has no registration for a provider that is not configured, and must never be asked for one.
        if (!ProviderNames.IsName(name) || !catalog.IsOffered(name))
        {
            return ApiResults.NotFound();
        }

        var query = http.Request.Query;
        var invite = query[InviteParameter];
        var purposeText = query[PurposeParameter];
        if (invite.Count > 1 || purposeText.Count > 1 || (invite.Count == 1 && purposeText.Count == 1))
        {
            return ApiResults.InvalidRequest();
        }

        var purpose = ProviderTicketPurpose.SignIn;
        string? inviteHash = null;
        if (invite.Count == 1)
        {
            if (!IsLinkToken(invite[0]))
            {
                return ApiResults.InvalidRequest();
            }

            purpose = ProviderTicketPurpose.Invite;
            inviteHash = Convert.ToHexString(EmailTokens.HashOf(invite[0]!));
        }
        else if (purposeText.Count == 1)
        {
            if (!string.Equals(purposeText[0], ProviderPurposes.ReauthName, StringComparison.Ordinal))
            {
                return ApiResults.InvalidRequest();
            }

            purpose = ProviderTicketPurpose.Reauth;
        }

        // RedirectUri is set on purpose: left out, OpenIddict keeps the address of this request, the invitation token included, in the state.
        var properties = new AuthenticationProperties { RedirectUri = "/" };
        properties.Items[ClientProperties.ProviderName] = name;
        properties.Items[ProviderStateItems.Purpose] = ProviderPurposes.NameOf(purpose);
        properties.Items[ProviderStateItems.Provider] = name;
        if (inviteHash is not null)
        {
            properties.Items[ProviderStateItems.InviteHash] = inviteHash;
        }

        if (purpose == ProviderTicketPurpose.Reauth)
        {
            // A fresh sign-in at the provider (Settled 26); Auth-Core does not check that one happened (residual risk 57).
            if (name == ProviderNames.Microsoft)
            {
                properties.Parameters["prompt"] = "login";
            }
            else
            {
                properties.Parameters["max_age"] = 0;
            }
        }
        else
        {
            properties.Parameters["prompt"] = "select_account";
        }

        return Results.Challenge(properties, [OpenIddictClientAspNetCoreDefaults.AuthenticationScheme]);
    }

    /// <summary>A link token of spec 0004: 43 base64url characters.</summary>
    internal static bool IsLinkToken(string? token) =>
        token is { Length: 43 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
