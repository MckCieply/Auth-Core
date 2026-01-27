using Auth.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Auth.Infrastructure.Persistence;

public class AuthDbContext(DbContextOptions<AuthDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<LoginStreak> LoginStreaks => Set<LoginStreak>();

    public DbSet<EmailToken> EmailTokens => Set<EmailToken>();

    public DbSet<MailRequest> MailRequests => Set<MailRequest>();

    public DbSet<MailRequestLimit> MailRequestLimits => Set<MailRequestLimit>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        base.OnModelCreating(builder);

        builder.Entity<LoginStreak>(streak =>
        {
            streak.HasKey(s => s.IdentifierHash);
            // Pruning selects by age.
            streak.HasIndex(s => s.LastAttemptAt);
        });

        builder.Entity<EmailToken>(token =>
        {
            // At most one token per user and kind: issuing a new one replaces the row.
            token.HasKey(t => new { t.UserId, t.Kind });
            token.HasOne<ApplicationUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
            // A token is looked up by its hash when it is used.
            token.HasIndex(t => t.TokenHash).IsUnique();
            // Pruning selects by expiry.
            token.HasIndex(t => t.ExpiresAt);
        });

        builder.Entity<MailRequest>(request =>
        {
            request.HasKey(r => r.Id);
            // The dispatcher takes the rows that are due, oldest first.
            request.HasIndex(r => r.NextAttemptAt);
        });

        builder.Entity<MailRequestLimit>(limit =>
        {
            limit.HasKey(l => new { l.IdentifierHash, l.Kind });
            // Pruning selects by age.
            limit.HasIndex(l => l.LastAcceptedAt);
        });
    }
}
