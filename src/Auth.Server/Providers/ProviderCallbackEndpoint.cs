using System.Security.Claims;
using Auth.Infrastructure.Persistence;
using Auth.Server.Api;
using Auth.Server.Email;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Client.AspNetCore;
using ClientProperties = OpenIddict.Client.AspNetCore.OpenIddictClientAspNetCoreConstants.Properties;

namespace Auth.Server.Providers;

/// <summary>The codes of a failed callback, in the fragment (spec 0011 → the callback's table).</summary>
public static class ProviderCallbackErrors
{
    public const string Cancelled = "cancelled";
    public const string InvalidState = "invalid_state";
    public const string ProviderUnavailable = "provider_unavailable";
    public const string ProviderError = "provider_error";
}

/// <summary>
/// <c>GET /auth/providers/{name}/callback</c> (spec 0011). By the time it runs, OpenIddict's client has judged the request in the authentication
/// step, which runs before this endpoint: the state (its signature, its age, its single use, the request-forgery cookie of the browser that started), the code (redeemed
/// with the PKCE verifier), the identity token (signature, issuer, audience, nonce, expiry). This reads the outcome and always answers a redirect to
/// the product's return screen, never a page of its own and never the provider's <c>error_description</c>: a ticket and <c>auth_pt</c> on success,
/// one of four codes otherwise, and nothing stored then. Neither the outcome's items (they hold the raw tokens) nor the query is logged.
/// </summary>
public static partial class ProviderCallbackEndpoint
{
    private const string LogCategory = "Auth.Server.Providers.ProviderCallback";

    // The ends of OpenIddict's error addresses for the request-forgery cookie: missing, malformed, not the state's own.
    private static readonly string[] ForgeryCookieIds = ["/ID2129", "/ID2163", "/ID2164"];

    public static async Task<IResult> HandleAsync(
        HttpContext http, string name, ProviderCatalog catalog, ProviderSettings settings, AuthDbContext db, ILookupNormalizer normalizer,
        TimeProvider clock, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(normalizer);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(loggers);

        if (!ProviderNames.IsName(name) || settings.ReturnUrl is not { } returnUrl)
        {
            return ApiResults.NotFound();   // no provider of that name, or no screen to send anyone to
        }

        var logger = loggers.CreateLogger(LogCategory);
        var result = await http.AuthenticateAsync(OpenIddictClientAspNetCoreDefaults.AuthenticationScheme);
        var items = result.Properties?.Items;
        var purposeName = items is not null && items.TryGetValue(ProviderStateItems.Purpose, out var stated) ? stated : null;

        // OpenIddict presets a 400 on a failure: the answer is always this redirect, whatever it set.
        if (!result.Succeeded)
        {
            return Refuse(logger, returnUrl, name, CodeOf(items, purposeName), purposeName, ErrorOf(items));
        }

        if (!ProviderPurposes.TryParse(purposeName, out var purpose)
            || !items!.TryGetValue(ProviderStateItems.Provider, out var stateProvider)
            || !string.Equals(stateProvider, name, StringComparison.Ordinal))
        {
            return Refuse(logger, returnUrl, name, ProviderCallbackErrors.ProviderError, purposeName);   // never: the state is ours, and OpenIddict matched its path
        }

        if (!catalog.IsOffered(name))
        {
            return Refuse(logger, returnUrl, name, ProviderCallbackErrors.ProviderUnavailable, purposeName);   // dropped from the manifest meanwhile
        }

        var identityToken = result.Properties!.GetParameter<ClaimsPrincipal>(ClientProperties.BackchannelIdentityTokenPrincipal);
        if (ProviderIdentities.Read(name, identityToken, normalizer, checkMicrosoftIssuer: settings.TestIssuer is null) is not { } identity)
        {
            return Refuse(logger, returnUrl, name, ProviderCallbackErrors.ProviderError, purposeName);   // a claim the identity needs is missing
        }

        byte[]? inviteHash = null;
        if (purpose == ProviderTicketPurpose.Invite && !TryInviteHash(items!, out inviteHash))   // a successful outcome always has its properties
        {
            return Refuse(logger, returnUrl, name, ProviderCallbackErrors.ProviderError, purposeName);
        }

        var issued = await ProviderTickets.IssueAsync(
            db, new ProviderTicketDraft(purpose, identity, inviteHash), StorableTime.Now(clock), CancellationToken.None);
        ProviderBindingCookie.Append(http.Response, issued.Binding);
        var madeFor = ProviderPurposes.NameOf(purpose);
        LogTicketMade(logger, name, madeFor);
        return Results.Redirect(WithFragment(returnUrl, $"ticket={issued.Ticket}&purpose={madeFor}"));
    }

    /// <summary>
    /// The code of a failed outcome: <c>invalid_state</c> when the state could not be read, or the request-forgery cookie is missing or
    /// another browser's (ID2129: no cookie; ID2163: a malformed one; ID2164: a cookie whose value is not the state's, such as another browser's;
    /// the cookie is judged before the provider's answer, Amendment A3); <c>cancelled</c> for
    /// <c>access_denied</c>; <c>provider_error</c> for everything else. Never by the error alone: <c>invalid_request</c> and <c>invalid_token</c>
    /// each cover both families.
    /// </summary>
    private static string CodeOf(IDictionary<string, string?>? items, string? purposeName)
    {
        if (items is null || purposeName is null)
        {
            return ProviderCallbackErrors.InvalidState;
        }

        var uri = items.TryGetValue(ClientProperties.ErrorUri, out var found) ? found : null;
        if (uri is not null && ForgeryCookieIds.Any(id => uri.EndsWith(id, StringComparison.Ordinal)))
        {
            return ProviderCallbackErrors.InvalidState;
        }

        return items.TryGetValue(ClientProperties.Error, out var error) && string.Equals(error, "access_denied", StringComparison.Ordinal)
            ? ProviderCallbackErrors.Cancelled
            : ProviderCallbackErrors.ProviderError;
    }

    /// <summary>
    /// The OAuth error the client recorded for a failed outcome (<c>access_denied</c>, <c>invalid_grant</c>, <c>invalid_client</c>, ...): a code of
    /// the protocol, so that the log line of a lapsed secret names <c>invalid_client</c> (spec 0011 → the runbook). Never its description, never
    /// the request; anything that is not a short lower-case code (the error of an authorization answer is the caller's text) is "none".
    /// </summary>
    private static string ErrorOf(IDictionary<string, string?>? items) =>
        items is not null && items.TryGetValue(ClientProperties.Error, out var error)
            && error is { Length: > 0 and <= 64 } && error.All(c => c is (>= 'a' and <= 'z') or '_')
            ? error
            : "none";

    private static IResult Refuse(ILogger logger, Uri returnUrl, string provider, string code, string? purposeName, string error = "none")
    {
        LogRefused(logger, provider, code, error);
        var fragment = ProviderPurposes.TryParse(purposeName, out var purpose)
            ? $"error={code}&purpose={ProviderPurposes.NameOf(purpose)}"
            : "error=" + code;
        return Results.Redirect(WithFragment(returnUrl, fragment));
    }

    private static bool TryInviteHash(IDictionary<string, string?> items, out byte[]? hash)
    {
        hash = null;
        if (!items.TryGetValue(ProviderStateItems.InviteHash, out var hex) || hex is not { Length: 64 })
        {
            return false;
        }

        try
        {
            hash = Convert.FromHexString(hex);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>The return address with the fragment: a browser never sends a fragment to a server (spec 0011 → the callback).</summary>
    private static string WithFragment(Uri returnUrl, string fragment) => returnUrl.AbsoluteUri + "#" + fragment;

    [LoggerMessage(Level = LogLevel.Information, Message = "The {Provider} callback ended in {Code} (the client recorded the OAuth error {Error}).")]
    private static partial void LogRefused(ILogger logger, string provider, string code, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "The {Provider} callback made a ticket for {Purpose}.")]
    private static partial void LogTicketMade(ILogger logger, string provider, string purpose);
}
