using Auth.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace Auth.Server.Seeding;

/// <summary>
/// Creates the development users at host startup so there is someone to log in as. It is a no-op outside the
/// <c>Development</c> environment. The first user (<see cref="EmailKey"/>, <see cref="PasswordKey"/>) has a
/// confirmed email. The optional second one (<see cref="UnverifiedEmailKey"/>, <see cref="UnverifiedPasswordKey"/>)
/// has not: until invitations exist it is the only way to have an account that needs verification (spec 0004,
/// Decision 2). Each is created only when both of its settings are present. Seeding is idempotent and never resets
/// the password of an existing user.
/// </summary>
public static partial class DevUserSeeder
{
    public const string EmailKey = "Auth:DevSeed:Email";
    public const string PasswordKey = "Auth:DevSeed:Password";
    public const string UnverifiedEmailKey = "Auth:DevSeed:UnverifiedEmail";
    public const string UnverifiedPasswordKey = "Auth:DevSeed:UnverifiedPassword";

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!services.GetRequiredService<IHostEnvironment>().IsDevelopment())
        {
            return;
        }

        var configuration = services.GetRequiredService<IConfiguration>();
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DevUserSeeder));

        await SeedOneAsync(users, logger, configuration[EmailKey], configuration[PasswordKey], emailConfirmed: true, ct);
        await SeedOneAsync(users, logger, configuration[UnverifiedEmailKey], configuration[UnverifiedPasswordKey], emailConfirmed: false, ct);
    }

    private static async Task SeedOneAsync(
        UserManager<ApplicationUser> users, ILogger logger, string? email, string? password, bool emailConfirmed, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        ct.ThrowIfCancellationRequested();
        if (await users.FindByEmailAsync(email) is not null)
        {
            return;
        }

        var result = await users.CreateAsync(
            new ApplicationUser { UserName = email, Email = email, EmailConfirmed = emailConfirmed },
            password);
        if (!result.Succeeded)
        {
            // Codes only: descriptions can echo policy details, and the password must never be logged.
            throw new InvalidOperationException(
                "Could not seed the development user: " + string.Join(", ", result.Errors.Select(e => e.Code)));
        }

        LogSeeded(logger, email);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded development user {Email}")]
    private static partial void LogSeeded(ILogger logger, string email);
}
