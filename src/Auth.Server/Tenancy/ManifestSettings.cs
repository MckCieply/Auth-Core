namespace Auth.Server.Tenancy;

/// <summary>Where the product's manifest is. The path is configuration; what the file says is the product's.</summary>
public sealed record ManifestSettings(string Path)
{
    public const string PathKey = "Auth:Manifest:Path";

    /// <summary>
    /// A relative path is taken from the application's content root. The path itself is required like every other
    /// setting; whether the file is good is not checked here, because a broken manifest never stops the service.
    /// </summary>
    /// <exception cref="InvalidOperationException">The path is missing or blank.</exception>
    public static ManifestSettings Load(IConfiguration configuration, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(contentRoot);

        var path = configuration[PathKey];
        if (string.IsNullOrWhiteSpace(path))
        {
            // Name the key only.
            throw new InvalidOperationException($"Configuration value '{PathKey}' is missing or blank.");
        }

        return new ManifestSettings(System.IO.Path.GetFullPath(path, contentRoot));
    }
}
