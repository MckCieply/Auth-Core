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

    public DbSet<Company> Companies => Set<Company>();

    public DbSet<CompanyRole> CompanyRoles => Set<CompanyRole>();

    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<Invite> Invites => Set<Invite>();

    public DbSet<ActiveManifest> ActiveManifests => Set<ActiveManifest>();

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

        builder.Entity<Company>(company =>
        {
            company.HasKey(c => c.Id);
            company.Property(c => c.Name).HasMaxLength(100);
        });

        builder.Entity<CompanyRole>(role =>
        {
            role.HasKey(r => r.Id);
            role.Property(r => r.Name).HasMaxLength(100);
            role.Property(r => r.NormalizedName).HasMaxLength(100);
            role.HasOne<Company>().WithMany().HasForeignKey(r => r.CompanyId).OnDelete(DeleteBehavior.Cascade);
            // The key other tables point at, so that a membership or an invitation can only name a role of its own company.
            role.HasAlternateKey(r => new { r.CompanyId, r.Id });
            // Role names are unique within a company, whatever their case.
            role.HasIndex(r => new { r.CompanyId, r.NormalizedName }).IsUnique();
        });

        builder.Entity<Membership>(membership =>
        {
            // The key allows several companies per user (spec 0005 → Concepts); the code lets a user have one.
            membership.HasKey(m => new { m.UserId, m.CompanyId });
            membership.HasOne<ApplicationUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
            membership.HasOne<Company>().WithMany().HasForeignKey(m => m.CompanyId).OnDelete(DeleteBehavior.Cascade);
            // A role is never deleted from under a member: the API refuses first, and the database agrees.
            membership.HasOne<CompanyRole>().WithMany()
                .HasForeignKey(m => new { m.CompanyId, m.RoleId })
                .HasPrincipalKey(r => new { r.CompanyId, r.Id })
                .OnDelete(DeleteBehavior.Restrict);
            // The foreign key to the role gets an index on (CompanyId, RoleId): it serves the member list of a company
            // and the count of members of a role.
        });

        builder.Entity<Invite>(invite =>
        {
            invite.HasKey(i => i.Id);
            invite.HasOne<Company>().WithMany().HasForeignKey(i => i.CompanyId).OnDelete(DeleteBehavior.Cascade);
            invite.HasOne<CompanyRole>().WithMany()
                .HasForeignKey(i => new { i.CompanyId, i.RoleId })
                .HasPrincipalKey(r => new { r.CompanyId, r.Id })
                .OnDelete(DeleteBehavior.Restrict);
            invite.HasOne<ApplicationUser>().WithMany().HasForeignKey(i => i.InvitedBy).OnDelete(DeleteBehavior.SetNull);
            // At most one invitation per company and address; an expired one is replaced, not kept beside the new one.
            invite.HasIndex(i => new { i.CompanyId, i.NormalizedEmail }).IsUnique();
            // A link is looked up by its hash; rows without a token yet (null) do not collide.
            invite.HasIndex(i => i.TokenHash).IsUnique();
            // Pruning selects by expiry.
            invite.HasIndex(i => i.ExpiresAt);
        });

        builder.Entity<ActiveManifest>(manifest =>
        {
            manifest.HasKey(m => m.Id);
            manifest.Property(m => m.Id).ValueGeneratedNever();
        });
    }
}
