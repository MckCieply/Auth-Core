using Auth.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace Auth.Server.Seeding;

/// <summary>
/// Creates one development user at host startup so there is someone to log in as. It is a no-op outside
/// the <c>Development</c> environment and unless both <see cref="EmailKey"/> and <see cref="PasswordKey"/>
/// are configured. It is idempotent and never resets the password of an existing user.
/// </summary>
public static partial class DevUserSeeder
{
    public const string EmailKey = "Auth:DevSeed:Email";
    public const string PasswordKey = "Auth:DevSeed:Password";

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!services.GetRequiredService<IHostEnvironment>().IsDevelopment())
        {
            return;
        }

        var configuration = services.GetRequiredService<IConfiguration>();
        var email = configuration[EmailKey];
        var password = configuration[PasswordKey];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        using var scope = services.CreateScope();
        ct.ThrowIfCancellationRequested();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (await users.FindByEmailAsync(email) is not null)
        {
            return;
        }

        var result = await users.CreateAsync(
            new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true },
            password);
        if (!result.Succeeded)
        {
            // Codes only: descriptions can echo policy details, and the password must never be logged.
            throw new InvalidOperationException(
                "Could not seed the development user: " + string.Join(", ", result.Errors.Select(e => e.Code)));
        }

        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DevUserSeeder));
        LogSeeded(logger, email);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded development user {Email}")]
    private static partial void LogSeeded(ILogger logger, string email);
}
