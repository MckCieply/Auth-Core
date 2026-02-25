using System.Globalization;

namespace Auth.Server.Audit;

/// <summary><c>Auth:Audit:RetentionDays</c>: how long a row of the audit log is kept (default 90, at least 1; blank means the default).</summary>
public sealed class AuditSettings
{
    public const string RetentionDaysKey = "Auth:Audit:RetentionDays";
    public const int DefaultRetentionDays = 90;

    public AuditSettings(int retentionDays)
    {
        if (retentionDays < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionDays), retentionDays, "The retention is at least one day.");
        }

        RetentionDays = retentionDays;
    }

    public int RetentionDays { get; }

    /// <exception cref="InvalidOperationException">The value is not a whole number of at least 1; the message names the key.</exception>
    public static AuditSettings Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration[RetentionDaysKey] is not { } text || string.IsNullOrWhiteSpace(text))
        {
            return new AuditSettings(DefaultRetentionDays);
        }

        return int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days >= 1
            ? new AuditSettings(days)
            : throw new InvalidOperationException($"Configuration value '{RetentionDaysKey}' must be a whole number of at least 1.");
    }
}
