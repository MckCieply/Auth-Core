using System.Globalization;

namespace Auth.Server.Tenancy;

/// <summary>
/// <c>Auth:Tenancy:MaxCompaniesPerAccount</c> (env <c>AUTH_MAX_COMPANIES_PER_ACCOUNT</c>): how many company memberships an account may hold
/// before it cannot create another (invitations may exceed it); a whole number from 1 to 1000, 20 when absent or blank. Read as a string, as
/// <c>RateLimitSettings</c> does: compose passes an empty value for a variable that is not set. Loaded by the service only, never by the CLI.
/// </summary>
public sealed record CompanyLimitSettings(int MaxCompaniesPerAccount)
{
    public const string Key = "Auth:Tenancy:MaxCompaniesPerAccount";
    public const int Default = 20;

    private const int Smallest = 1;
    private const int Largest = 1000;

    /// <exception cref="InvalidOperationException">The value is not a whole number from 1 to 1000; the message names the key.</exception>
    public static CompanyLimitSettings Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration[Key] is not { } text || string.IsNullOrWhiteSpace(text))
        {
            return new CompanyLimitSettings(Default);
        }

        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || limit < Smallest || limit > Largest)
        {
            throw new InvalidOperationException($"Configuration value '{Key}' must be a whole number from {Smallest} to {Largest}.");
        }

        return new CompanyLimitSettings(limit);
    }
}
