using AutoMapper;
using Marketing.Application.DTOs.Tenants;
using Marketing.DataAccess.Entities;

namespace Marketing.Application.Mappings;

/// <summary>
/// Entity to DTO mappings for tenants.
/// <para>
/// Mappings are entity-to-DTO only, never the reverse. Mapping a request DTO back onto a tracked
/// entity is how mass-assignment bugs get in - it would happily overwrite <c>TenantId</c>,
/// <c>IsDeleted</c> or the audit columns. Write paths assign the fields they mean to change.
/// </para>
/// </summary>
public sealed class TenantMappingProfile : Profile
{
    /// <summary>Initialises a new instance.</summary>
    public TenantMappingProfile()
    {
        CreateMap<Tenant, TenantSummaryResponse>()
            .ForCtorParam(
                nameof(TenantSummaryResponse.UserCount),
                options => options.MapFrom(tenant => tenant.Users.Count(user => !user.IsDeleted)));
    }
}
