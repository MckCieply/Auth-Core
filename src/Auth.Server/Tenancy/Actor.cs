namespace Auth.Server.Tenancy;

/// <summary>
/// Who is doing something to a company: a member, with the role the database gave them a moment ago, or the operator
/// at the command line. Safety rule 1 (nobody grants more than they hold) binds a member and not the operator.
/// </summary>
public sealed class Actor
{
    private Actor(TenantContext? member) => Member = member;

    /// <summary>Whoever runs the instance: not a member of any company, and exempt from safety rule 1 (spec 0005 → Concepts).</summary>
    public static Actor Operator { get; } = new(null);

    public static Actor Of(TenantContext member)
    {
        ArgumentNullException.ThrowIfNull(member);

        return new Actor(member);
    }

    /// <summary>The acting member; <see langword="null"/> for the operator.</summary>
    public TenantContext? Member { get; }

    public bool IsOperator => Member is null;

    /// <summary>The member's user id; <see langword="null"/> for the operator.</summary>
    public Guid? UserId => Member?.UserId;

    /// <summary>
    /// Safety rule 1: whether this actor may give, create or edit a role that holds <paramref name="stored"/>. Every
    /// permission the role ends up with must be one the actor holds, and a role with <c>*</c> needs an actor whose own
    /// role holds <c>*</c>. Names that have left the catalog grant nothing and do not count.
    /// </summary>
    public bool MayGrant(IEnumerable<string> stored, PermissionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(catalog);

        if (Member is null)
        {
            return true;
        }

        var list = stored as IReadOnlyCollection<string> ?? [.. stored];
        return list.Contains(PermissionCatalog.All, StringComparer.Ordinal)
            ? Member.HoldsAll
            : catalog.Expand(list).All(Member.Holds);
    }
}
