using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Auth.Server.Signup;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>Makes a pending sign-up straight in the database, with its link already issued, for tests that are not about the mail.</summary>
public static class PendingSignupSeed
{
    /// <summary>The pending sign-up of the address as the dispatcher leaves it after sending; returns the token in clear.</summary>
    public static async Task<string> IssueAsync(IServiceProvider services, string email, string companyName)
    {
        ArgumentNullException.ThrowIfNull(services);

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var normalized = scope.ServiceProvider.GetRequiredService<ILookupNormalizer>().NormalizeEmail(email);
        var now = StorableTime.Now(services.GetRequiredService<TimeProvider>());
        var cancellation = TestContext.Current.CancellationToken;
        await PendingSignups.UpsertAsync(db, normalized, email, companyName, now, cancellation);
        return await PendingSignups.IssueAsync(db, normalized, now, cancellation);
    }
}
