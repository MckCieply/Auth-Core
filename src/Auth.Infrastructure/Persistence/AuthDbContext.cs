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

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<PendingSignup> PendingSignups => Set<PendingSignup>();

    public DbSet<ProviderTicket> ProviderTickets => Set<ProviderTicket>();

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
            // Spec 0010: every row made before the column is an organization. Without the default the migration would give them 0.
            // Zero is EF's sentinel for "not set" (the database default applies); saying so avoids EF's start-up warning about a database default.
            company.Property(c => c.Kind).HasDefaultValue(CompanyKind.Organization).HasSentinel((CompanyKind)0);
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

        // Spec 0009. Not an account: no foreign key to one.
        builder.Entity<PendingSignup>(pending =>
        {
            pending.HasKey(p => p.NormalizedEmail);
            pending.Property(p => p.NormalizedEmail).HasMaxLength(256);
            pending.Property(p => p.Email).HasMaxLength(256);
            pending.Property(p => p.CompanyName).HasMaxLength(100);
            // Spec 0010: every row made before the column is an organization sign-up.
            pending.Property(p => p.Kind).HasDefaultValue(SignupKind.Organization).HasSentinel((SignupKind)0);
            // A link is looked up by its hash when it is used; rows without a link yet (null) do not collide.
            pending.HasIndex(p => p.TokenHash).IsUnique();
            // The clean-up selects by expiry.
            pending.HasIndex(p => p.ExpiresAt);
        });

        // Spec 0011. A ticket names no account until it is used: no foreign key.
        builder.Entity<ProviderTicket>(ticket =>
        {
            ticket.HasKey(t => t.Id);
            ticket.Property(t => t.Provider).HasMaxLength(20);
            ticket.Property(t => t.IdentityKey).HasMaxLength(255);
            ticket.Property(t => t.Email).HasColumnType("text");   // any `email` claim is kept (spec 0011 → the identity): no length limit
            // A ticket is looked up by its hash, and one statement uses it up.
            ticket.HasIndex(t => t.TicketHash).IsUnique();
            // The clean-up selects by expiry.
            ticket.HasIndex(t => t.ExpiresAt);
        });

        // Spec 0008 → Audit log. The names are the contract's (snake_case): the backup runbook queries them. No foreign keys on purpose.
        builder.Entity<AuditEvent>(audit =>
        {
            audit.ToTable("audit_events");
            audit.HasKey(a => a.Id);
            audit.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
            audit.Property(a => a.OccurredAt).HasColumnName("occurred_at");
            audit.Property(a => a.Kind).HasColumnName("kind").HasMaxLength(40);
            audit.Property(a => a.ActorUserId).HasColumnName("actor_user_id");
            audit.Property(a => a.SubjectUserId).HasColumnName("subject_user_id");
            audit.Property(a => a.SubjectEmail).HasColumnName("subject_email").HasMaxLength(256);
            audit.Property(a => a.OrgId).HasColumnName("org_id");
            audit.Property(a => a.OrgName).HasColumnName("org_name").HasMaxLength(100);
            audit.Property(a => a.TargetId).HasColumnName("target_id");
            audit.Property(a => a.ClientIp).HasColumnName("client_ip").HasMaxLength(64);
            audit.Property(a => a.Details).HasColumnName("details").HasColumnType("jsonb");
            // Pruning selects by age; the runbook's queries select by account, company and address.
            audit.HasIndex(a => a.OccurredAt);
            audit.HasIndex(a => a.SubjectUserId);
            audit.HasIndex(a => a.ActorUserId);
            audit.HasIndex(a => a.OrgId);
            audit.HasIndex(a => a.ClientIp);
        });
    }
}
