using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Auth.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringKey = "ConnectionStrings:Auth";

    public static IServiceCollection AddAuthPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration[ConnectionStringKey];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Name the key only; never echo a connection-string value.
            throw new InvalidOperationException($"Configuration value '{ConnectionStringKey}' is missing or blank.");
        }

        services.AddDbContext<AuthDbContext>(options =>
        {
            options.UseNpgsql(connectionString);
            options.UseOpenIddict();
        });

        services
            .AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                // The user name of an account is its email (invitations, spec 0005, create accounts for any address a
                // member types), and Identity's default list of allowed characters would refuse `zażółć@example.com`.
                options.User.AllowedUserNameCharacters = string.Empty;
                // Spec 0004, Decision 9. Applies when a password is set; a login never checks it.
                options.Password.RequiredLength = 8;
                options.Password.RequireUppercase = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireDigit = true;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredUniqueChars = 1;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AuthDbContext>();

        // Replaces Identity's validator rather than adding to it: the built-in one would still refuse a password
        // whose only uppercase letter is not A-Z.
        services.Replace(ServiceDescriptor.Scoped<IPasswordValidator<ApplicationUser>, UnicodePasswordValidator>());

        return services;
    }
}
