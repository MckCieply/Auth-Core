namespace Auth.Server.Tenancy;

public static class TenancyServices
{
    /// <summary>
    /// The registrations shared by the host and the operator commands: what decides which manifest is active, and the
    /// services that work on companies, members and invitations.
    /// </summary>
    public static IServiceCollection AddTenancy(this IServiceCollection services, IConfiguration configuration, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(ManifestSettings.Load(configuration, contentRoot));
        services.AddSingleton<ManifestHolder>();
        services.AddSingleton<ManifestActivator>();
        services.AddScoped<CompanyService>();
        services.AddScoped<MembershipReader>();
        services.AddScoped<CompanyGuard>();
        services.AddScoped<InvitationService>();
        services.AddScoped<InviteAcceptance>();
        services.AddScoped<MemberService>();
        services.AddScoped<RoleService>();
        return services;
    }
}
