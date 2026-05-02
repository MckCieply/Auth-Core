using Auth.Server.Api;
using Auth.Server.Providers;

namespace Auth.Server.Signup;

/// <summary>
/// <c>GET /auth/options</c> (spec 0009): what a product's screens may offer, so that a product does not repeat its manifest in its
/// frontend. No authentication; the same answer for everyone.
/// </summary>
public static class SignupOptionsEndpoint
{
    public const string Path = "/auth/options";

    public static IResult Handle(SignupGate gate, ProviderCatalog providers)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(providers);

        return ApiResults.Ok(new OptionsResponse(gate.ModeName, gate.PrivateModeName, providers.Offered));
    }
}
