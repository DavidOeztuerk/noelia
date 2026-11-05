namespace Noelia.Core.Identity;

/// <summary>
/// Who issued the current request, and in what capacity.
/// </summary>
/// <remarks>
/// Built once from the validated token at the middleware boundary. Read the
/// principal from <see cref="ICurrentPrincipal"/> downstream rather than
/// inspecting claims again.
/// <para>
/// Always a person. A service calling a door has no principal; the handler of a door
/// that wants to know who called reads <c>HttpContext.User</c>.
/// </para>
/// </remarks>
public sealed record Principal
{
    /// <summary>The acting person. Unchanged by which company they act for.</summary>
    public required SubjectId Subject { get; init; }

    public required Capacity Acting { get; init; }

    /// <summary>Creates a principal acting for itself.</summary>
    public static Principal Person(SubjectId subject) =>
        new() { Subject = subject, Acting = Capacity.AsSelf.Instance };

    /// <summary>Creates a principal acting on behalf of <paramref name="tenant"/>.</summary>
    public static Principal Company(SubjectId subject, TenantId tenant) =>
        new() { Subject = subject, Acting = new Capacity.ForCompany(tenant) };

    /// <summary>
    /// The tenant when acting for a company, otherwise <see cref="TenantId.None"/>.
    /// </summary>
    /// <remarks>
    /// Intended for query filters, which need a total value to compare against.
    /// In domain logic match on <see cref="Capacity.ForCompany"/> instead, so the
    /// compiler makes you handle the other case.
    /// </remarks>
    public TenantId TenantForQueryFilter =>
        Acting is Capacity.ForCompany company ? company.Tenant : TenantId.None;

    /// <summary>Gets the tenant, or returns false when acting as a person.</summary>
    public bool TryGetTenant(out TenantId tenant)
    {
        if (Acting is Capacity.ForCompany company)
        {
            tenant = company.Tenant;
            return true;
        }

        tenant = TenantId.None;
        return false;
    }

    public override string ToString() => $"{Subject} ({Acting})";
}
