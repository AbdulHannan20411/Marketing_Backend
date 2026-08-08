namespace Marketing.DataAccess.Entities;

/// <summary>
/// Marks an entity that the tenant query filter applies to.
/// <para>
/// <c>ApplicationDbContext</c> discovers every implementor at model-build time and attaches the
/// filter automatically. That is deliberate: tenant isolation must be structural, not something
/// each new repository has to remember.
/// </para>
/// <para>
/// <see cref="BaseEntity.TenantId"/> may still be null here, because a few tables legitimately hold
/// platform-level rows alongside tenant rows - a platform administrator's user record, their role
/// assignment, their session. Those rows are invisible to tenant operators (null never equals a
/// tenant id) and visible to platform administrators (who bypass the filter), which is exactly the
/// behaviour wanted. Entities where a missing tenant is always a bug should implement
/// <see cref="IRequiresTenant"/> instead.
/// </para>
/// </summary>
public interface ITenantScoped
{
    /// <summary>Owning tenant, or null for a platform-level row.</summary>
    public long? TenantId { get; set; }
}

/// <summary>
/// Marks an entity that must always belong to a tenant - contacts, groups, tags, templates,
/// campaigns and everything else in the business domain.
/// <para>
/// The auditing interceptor refuses to persist one of these with no tenant. Without that check the
/// row would simply become invisible to every query, which surfaces much later and much more
/// confusingly than a failed write.
/// </para>
/// </summary>
public interface IRequiresTenant : ITenantScoped
{
}
