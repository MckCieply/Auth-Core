using Auth.Server.Api;

namespace Auth.Server.Signup;

/// <summary>
/// <c>GET /auth/options</c> (spec 0009): what a product's screens may offer, so that a product does not repeat its manifest in its
/// frontend. No authentication; the same answer for everyone.
/// </summary>
public static class SignupOptionsEndpoint
{
    public const string Path = "/auth/options";

    public static IResult Handle(SignupGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);

        return ApiResults.Ok(new OptionsResponse(gate.ModeName));
    }
}
