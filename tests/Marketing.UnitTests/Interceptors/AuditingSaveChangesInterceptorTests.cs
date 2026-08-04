using AwesomeAssertions;
using Marketing.Common.Exceptions;
using Marketing.DataAccess.Entities;
using Marketing.DataAccess.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Marketing.UnitTests.Interceptors;

public sealed class AuditingSaveChangesInterceptorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);
    private static readonly Guid ActingUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly StubTenantContext _tenantContext = new() { TenantId = TenantId };
    private readonly StubCurrentUser _currentUser = new() { UserId = ActingUserId };
    private readonly FixedDateTimeProvider _clock = new(Now);
    private readonly TestApplicationDbContext _context;
    private readonly AuditingSaveChangesInterceptor _interceptor;

    public AuditingSaveChangesInterceptorTests()
    {
        _context = TestDbContextFactory.Create(_tenantContext);
        _interceptor = new AuditingSaveChangesInterceptor(_currentUser, _tenantContext, _clock);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public void Added_entity_is_stamped_with_creator_timestamp_and_ambient_tenant()
    {
        var user = NewUser();
        _context.Users.Add(user);

        Intercept();

        user.CreatedBy.Should().Be(ActingUserId);
        user.CreatedOn.Should().Be(Now);
        user.TenantId.Should().Be(TenantId);
        user.IsDeleted.Should().BeFalse();
        user.ModifiedOn.Should().BeNull();
    }

    [Fact]
    public void Explicitly_assigned_tenant_is_not_overwritten_by_the_ambient_tenant()
    {
        var otherTenant = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var user = NewUser();
        user.TenantId = otherTenant;

        _context.Users.Add(user);

        Intercept();

        // A platform administrator creating a user inside a tenant, or a webhook that resolved the
        // tenant from a phone number, must not have their choice silently replaced.
        user.TenantId.Should().Be(otherTenant);
    }

    [Fact]
    public void Modified_entity_is_stamped_and_creation_columns_are_left_untouched()
    {
        var user = AttachExistingUser();

        user.DisplayName = "Renamed";
        _context.Entry(user).State = EntityState.Modified;

        Intercept();

        user.ModifiedBy.Should().Be(ActingUserId);
        user.ModifiedOn.Should().Be(Now);

        var entry = _context.Entry(user);
        entry.Property(nameof(BaseEntity.CreatedBy)).IsModified.Should().BeFalse();
        entry.Property(nameof(BaseEntity.CreatedOn)).IsModified.Should().BeFalse();
    }

    [Fact]
    public void Reassigning_an_entity_to_another_tenant_is_refused()
    {
        var user = AttachExistingUser();

        user.TenantId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        _context.Entry(user).State = EntityState.Modified;

        var act = Intercept;

        // The single most damaging bug this codebase could ship is one that moves a row across the
        // isolation boundary, so it fails loudly rather than being written.
        act.Should().Throw<ForbiddenException>()
            .WithMessage("*owning tenant*cannot be changed*");
    }

    [Fact]
    public void Delete_is_rewritten_as_a_soft_delete_touching_only_the_delete_columns()
    {
        var user = AttachExistingUser();

        _context.Users.Remove(user);

        Intercept();

        _context.Entry(user).State.Should().Be(EntityState.Modified);
        user.IsDeleted.Should().BeTrue();
        user.DeletedBy.Should().Be(ActingUserId);
        user.DeletedOn.Should().Be(Now);

        var modified = _context.Entry(user).Properties
            .Where(property => property.IsModified)
            .Select(property => property.Metadata.Name)
            .ToArray();

        // Marking the whole entity dirty would write every column back, which can resurrect stale
        // in-memory values that were never meant to be saved.
        modified.Should().BeEquivalentTo(
            nameof(BaseEntity.IsDeleted),
            nameof(BaseEntity.DeletedBy),
            nameof(BaseEntity.DeletedOn),
            nameof(BaseEntity.ModifiedBy),
            nameof(BaseEntity.ModifiedOn));
    }

    [Fact]
    public void Entities_requiring_a_tenant_are_refused_when_none_can_be_resolved()
    {
        _tenantContext.TenantId = null;

        _context.Probes.Add(new TenantMandatoryProbe());

        var act = Intercept;

        act.Should().Throw<TenantResolutionException>();
    }

    [Fact]
    public void Unauthenticated_writes_are_attributed_to_the_system_identity()
    {
        _currentUser.UserId = null;

        var user = NewUser();
        _context.Users.Add(user);

        Intercept();

        user.CreatedBy.Should().Be(Marketing.Common.Constants.SystemIdentity.UserId);
    }

    private void Intercept() =>
        _interceptor.SavingChanges(new DbContextEventData(null!, null!, _context), default);

    private static User NewUser() => new()
    {
        Email = "operator@example.com",
        NormalizedEmail = "operator@example.com",
        DisplayName = "Operator",
        PasswordHash = "pbkdf2-sha256$1000$c2FsdA==$aGFzaA==",
    };

    private User AttachExistingUser()
    {
        var user = NewUser();
        user.TenantId = TenantId;
        user.CreatedBy = ActingUserId;
        user.CreatedOn = Now.AddDays(-30);

        _context.Attach(user);

        return user;
    }
}
