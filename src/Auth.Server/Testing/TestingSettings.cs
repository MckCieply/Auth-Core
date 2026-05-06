namespace Auth.Server.Testing;

/// <summary>
/// Spec 0012 → Configuration: <c>Auth:Testing:WindowEndpoint</c>, Development only. <c>true</c> maps the testing endpoint, so that a browser test
/// reaches a closed window without moving the server's clock (Settled 22). Any value outside Development stops the start, naming the key, as the
/// test issuer does: a production instance must never offer it.
/// </summary>
public sealed class TestingSettings
{
    public const string WindowEndpointKey = "Auth:Testing:WindowEndpoint";

    public bool WindowEndpoint { get; private init; }

    /// <exception cref="InvalidOperationException">The key is set outside Development, or to something other than true or false.</exception>
    public static TestingSettings Load(IConfiguration configuration, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var text = configuration[WindowEndpointKey];
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TestingSettings();
        }

        if (!isDevelopment)
        {
            throw new InvalidOperationException($"Configuration value '{WindowEndpointKey}' is for the Development environment only: remove it.");
        }

        return bool.TryParse(text.Trim(), out var on)
            ? new TestingSettings { WindowEndpoint = on }
            : throw new InvalidOperationException($"Configuration value '{WindowEndpointKey}' must be true or false.");
    }
}
