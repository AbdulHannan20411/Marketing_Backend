using System.Security.Claims;
using AwesomeAssertions;
using Marketing.Application.Services.Security;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Infrastructure.Authentication;
using Marketing.Shared.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Marketing.UnitTests.Services;

/// <summary>
/// Previewing what one teammate can see.
/// </summary>
/// <remarks>
/// This is an authorisation boundary, so most of what follows is about refusals. The property the
/// whole feature rests on is that a preview can only ever <em>subtract</em> from what the caller
/// could already see - if it can be made to add, it is not a diagnostic, it is an escalation.
/// </remarks>
public sealed class ViewAsEmployeeTests : IDisposable
{
    private const long TenantId = 9301;
    private const long OtherTenantId = 9302;
    private const long AdminId = 91;
    private const long EmployeeId = 92;

    private readonly List<User> _users = [];
    private readonly List<ActivityEntry> _recorded = [];

    private readonly IUserRepository _userRepository = Substitute.For<IUserRepository>();
    private readonly IRepository<ActivityEntry> _activity = Substitute.For<IRepository<ActivityEntry>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ViewAsContext _viewAs = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    private readonly StubCurrentUser _caller = new()
    {
        UserId = AdminId,
        DisplayName = "Honey",
        SessionId = Guid.NewGuid(),
        Roles = [Roles.Admin],
        Permissions = [.. Permissions.ForRole(Roles.Admin)],
    };

    private StubTenantContext _tenant = new() { TenantId = TenantId };

    public void Dispose() => _cache.Dispose();

    public ViewAsEmployeeTests()
    {
        _userRepository
            .FindWithRolesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(_users.FirstOrDefault(user => user.Id == call.Arg<long>())));

        _activity.When(repository => repository.Add(Arg.Any<ActivityEntry>()))
            .Do(call => _recorded.Add(call.Arg<ActivityEntry>()!));
    }

    private ViewAsResolver CreateResolver() =>
        new(
            _userRepository,
            _activity,
            _unitOfWork,
            _caller,
            _tenant,
            _viewAs,
            _cache,
            new FixedDateTimeProvider(new DateTimeOffset(2026, 9, 26, 9, 0, 0, TimeSpan.Zero)),
            NullLogger<ViewAsResolver>.Instance);

    private User Member(
        long id,
        long? tenantId,
        string role,
        string name = "Ayesha Khan",
        params (string Permission, bool Granted)[] overrides)
    {
        var user = new User
        {
            Id = id,
            TenantId = tenantId,
            Email = $"{id}@example.com",
            NormalizedEmail = $"{id}@EXAMPLE.COM",
            DisplayName = name,
            PasswordHash = "pbkdf2-sha256$1000$c2FsdA==$aGFzaA==",
            UserRoles =
            [
                new UserRole
                {
                    UserId = id,
                    Role = new Role
                    {
                        Name = role,
                        NormalizedName = role.ToUpperInvariant(),
                        Description = role,
                        Permissions = [.. Permissions.ForRole(role)],
                    },
                },
            ],
            PermissionOverrides =
            [
                .. overrides.Select(entry => new UserPermissionOverride
                {
                    UserId = id,
                    Permission = entry.Permission,
                    IsGranted = entry.Granted,
                }),
            ],
        };

        _users.Add(user);

        return user;
    }

    private static string Public(long employeeId) => PublicId.From(PublicId.Employee, employeeId);

    [Fact]
    public async Task No_parameter_leaves_the_caller_as_themselves()
    {
        using var scope = await CreateResolver()
            .EnterAsync(null, TestContext.Current.CancellationToken);

        _viewAs.IsPreviewing.Should().BeFalse();
        _recorded.Should().BeEmpty("nothing was looked at");
    }

    [Fact]
    public async Task An_admin_previewing_their_own_teammate_takes_that_teammates_permissions()
    {
        Member(EmployeeId, TenantId, Roles.Employee);

        using var scope = await CreateResolver()
            .EnterAsync(Public(EmployeeId), TestContext.Current.CancellationToken);

        var previewed = _viewAs.Previewing.Should().NotBeNull().And.Subject.As<ViewAsIdentity>();

        previewed.UserId.Should().Be(EmployeeId);
        previewed.Roles.Should().BeEquivalentTo([Roles.Employee]);
        previewed.Permissions.Should().BeEquivalentTo(Permissions.ForRole(Roles.Employee));
    }

    [Fact]
    public async Task The_administrator_role_is_not_carried_into_the_preview()
    {
        Member(EmployeeId, TenantId, Roles.Employee);

        using var scope = await CreateResolver()
            .EnterAsync(Public(EmployeeId), TestContext.Current.CancellationToken);

        // The single most important line here. Half the codebase asks "is this person an Admin"
        // to decide how wide a read is - every WhatsApp number, every conversation. Keeping the
        // caller's role would have shown them their own unrestricted view under the teammate's
        // name, which is the exact wrong answer to "why can't Ayesha see that conversation?".
        _viewAs.Previewing!.Roles.Should().NotContain(Roles.Admin);
    }

    [Fact]
    public async Task A_teammates_own_overrides_are_honoured()
    {
        Member(
            EmployeeId,
            TenantId,
            Roles.Employee,
            overrides: [(Permissions.Contacts.Delete, false)]);

        using var scope = await CreateResolver()
            .EnterAsync(Public(EmployeeId), TestContext.Current.CancellationToken);

        // Resolved through EffectivePermissions, the same function that mints their token, so the
        // preview cannot show a different answer from the one they get by signing in.
        _viewAs.Previewing!.Permissions.Should().NotContain(Permissions.Contacts.Delete);
    }

    [Fact]
    public async Task A_preview_can_never_grant_the_caller_something_they_lack()
    {
        // A colleague with an override the caller does not hold.
        _caller.Permissions = [Permissions.Contacts.View];

        Member(
            EmployeeId,
            TenantId,
            Roles.Employee,
            overrides: [(Permissions.Platform.Tenants, true)]);

        using var scope = await CreateResolver()
            .EnterAsync(Public(EmployeeId), TestContext.Current.CancellationToken);

        // Intersected with the caller's own. Without this, "view as" would be a way to borrow a
        // permission for the length of a request - an escalation wearing the clothes of a
        // diagnostic.
        _viewAs.Previewing!.Permissions.Should().NotContain(Permissions.Platform.Tenants);
        _viewAs.Previewing!.Permissions.Should().BeSubsetOf(_caller.Permissions);
    }

    [Fact]
    public async Task Somebody_who_is_not_an_administrator_cannot_preview_at_all()
    {
        _caller.Roles = [Roles.Employee];
        _caller.Permissions = [.. Permissions.ForRole(Roles.Employee)];

        Member(EmployeeId, TenantId, Roles.Employee);

        var call = async () => await CreateResolver()
            .EnterAsync(Public(EmployeeId), TestContext.Current.CancellationToken);

        await call.Should().ThrowAsync<ForbiddenException>();
        _viewAs.IsPreviewing.Should().BeFalse();
    }

    [Fact]
    public async Task Another_workspaces_employee_is_a_404_rather_than_a_403()
    {
        Member(EmployeeId, OtherTenantId, Roles.Employee);

        var call = async () => await CreateResolver()
            .EnterAsync(Public(EmployeeId), TestContext.Current.CancellationToken);

        // A 403 would confirm that the id names somebody real in another workspace, which is the
        // thing the answer is meant to withhold.
        await call.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task An_employee_who_does_not_exist_is_a_404()
    {
        var call = async () => await CreateResolver()
            .EnterAsync(Public(404404), TestContext.Current.CancellationToken);

        await call.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task An_identifier_of_the_wrong_kind_is_a_404()
    {
        Member(EmployeeId, TenantId, Roles.Employee);

        var call = async () => await CreateResolver()
            .EnterAsync(PublicId.From(PublicId.Contact, EmployeeId), TestContext.Current.CancellationToken);

        await call.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Platform_staff_cannot_be_previewed()
    {
        Member(EmployeeId, TenantId, Roles.SuperAdmin, name: "Platform Operator");

        var call = async () => await CreateResolver()
            .EnterAsync(Public(EmployeeId), TestContext.Current.CancellationToken);

        // Should already be impossible - platform staff hold no workspace - but the one thing
        // this feature must never do is widen, so it is refused explicitly as well.
        await call.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task A_platform_administrator_outside_every_workspace_cannot_preview()
    {
        _caller.Roles = [Roles.SuperAdmin];
        _tenant = new StubTenantContext { TenantId = null };

        Member(EmployeeId, TenantId, Roles.Employee);

        var call = async () => await CreateResolver()
            .EnterAsync(Public(EmployeeId), TestContext.Current.CancellationToken);

        // They must scope themselves to the workspace first, with ?adminId=. Previewing a
        // teammate from outside every workspace has no workspace to check membership against.
        await call.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Leaving_the_scope_ends_the_preview()
    {
        Member(EmployeeId, TenantId, Roles.Employee);

        var resolver = CreateResolver();

        using (await resolver.EnterAsync(Public(EmployeeId), TestContext.Current.CancellationToken))
        {
            _viewAs.IsPreviewing.Should().BeTrue();
        }

        // The request is over. Nothing after it is still previewing, and the next request on this
        // connection starts as whoever it is.
        _viewAs.IsPreviewing.Should().BeFalse();
    }

    [Fact]
    public async Task Looking_is_recorded_on_the_workspaces_own_activity_feed()
    {
        Member(EmployeeId, TenantId, Roles.Employee);

        using var scope = await CreateResolver()
            .EnterAsync(Public(EmployeeId), TestContext.Current.CancellationToken);

        var entry = _recorded.Should().ContainSingle().Which;

        // Where the person being previewed can find it, which is the point of recording it.
        entry.Actor.Should().Be("Honey");
        entry.Action.Should().Be("viewed the app as");
        entry.Subject.Should().Be("Ayesha Khan");
    }

    [Fact]
    public async Task Two_hundred_reads_in_one_sitting_record_one_entry()
    {
        Member(EmployeeId, TenantId, Roles.Employee);

        for (var index = 0; index < 5; index++)
        {
            using var scope = await CreateResolver()
                .EnterAsync(Public(EmployeeId), TestContext.Current.CancellationToken);
        }

        // The parameter rides every request. An entry per request would bury the workspace's
        // activity feed under one administrator's browsing, and the act worth recording is the
        // looking, not each page of it.
        _recorded.Should().ContainSingle();
    }
}

/// <summary>
/// What the previewed identity changes about the caller, and what it must not.
/// </summary>
public sealed class ViewAsCurrentUserTests
{
    private const long AdminId = 91;
    private const long EmployeeId = 92;

    private readonly ViewAsContext _viewAs = new();

    private HttpContextCurrentUser CreateUser()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("sub", AdminId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new Claim(ClaimTypes.Role, Roles.Admin),
                    new Claim(AppConstants.Claims.Permissions, Permissions.Platform.Tenants),
                    new Claim(AppConstants.Claims.Name, "Honey"),
                ],
                "Bearer",
                nameType: "sub",
                roleType: ClaimTypes.Role)),
        };

        return new HttpContextCurrentUser(new HttpContextAccessor { HttpContext = context }, _viewAs);
    }

    private IDisposable Preview() =>
        _viewAs.BeginScope(new ViewAsIdentity(
            EmployeeId, "Ayesha Khan", [Roles.Employee], [Permissions.Contacts.View]));

    [Fact]
    public void A_preview_changes_who_the_caller_counts_as_for_reading()
    {
        var user = CreateUser();

        using var preview = Preview();

        user.UserId.Should().Be(EmployeeId);
        user.Roles.Should().BeEquivalentTo([Roles.Employee]);
        user.IsInRole(Roles.Admin).Should().BeFalse();
        user.HasPermission(Permissions.Contacts.View).Should().BeTrue();
        user.HasPermission(Permissions.Platform.Tenants).Should().BeFalse();
    }

    [Fact]
    public void A_preview_never_changes_who_a_written_row_is_attributed_to()
    {
        var user = CreateUser();

        using var preview = Preview();

        // Writes are refused before they reach a service, so this should never come up. "Should
        // never" is not a guarantee, and the failure it would hide is a row in the audit trail
        // attributed to somebody who did not make it.
        user.AuditUserId.Should().Be(AdminId);
        user.DisplayName.Should().Be("Honey");
    }

    [Fact]
    public void A_preview_never_moves_the_session()
    {
        var user = CreateUser();

        using var preview = Preview();

        // Session revocation, sign-out and device management all key off this. A preview that
        // moved it would let an administrator end a colleague's session by accident.
        user.SessionId.Should().Be(CreateUser().SessionId);
        user.IsAuthenticated.Should().BeTrue();
    }

    [Fact]
    public void Without_a_preview_nothing_changes()
    {
        var user = CreateUser();

        user.UserId.Should().Be(AdminId);
        user.IsInRole(Roles.Admin).Should().BeTrue();
        user.HasPermission(Permissions.Platform.Tenants).Should().BeTrue();
    }

    [Fact]
    public void A_preview_cannot_be_nested()
    {
        using var first = Preview();

        var second = Preview;

        // There is no caller that needs it, and a second scope silently replacing the first is
        // how a preview ends up applying to the wrong person.
        second.Should().Throw<InvalidOperationException>();
    }
}
