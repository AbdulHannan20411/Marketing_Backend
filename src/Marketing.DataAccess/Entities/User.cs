using static Marketing.Common.Constants.AppConstants;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.DataAccess.Entities;

/// <summary>
/// A person who can sign in.
/// <para>
/// <see cref="BaseEntity.TenantId"/> is null for platform administrators and required for everyone
/// else; the tenant query filter therefore hides other tenants' users from a tenant operator while
/// still letting a platform administrator enumerate them.
/// </para>
/// </summary>
public sealed class User : BaseEntity, ITenantScoped
{
    /// <summary>Address as the user typed it, preserved for display.</summary>
    public required string Email { get; set; }

    /// <summary>
    /// Lowercased address used for lookup and uniqueness. Stored separately so the unique index is
    /// a plain B-tree on a normalised column rather than a functional index over collated text.
    /// </summary>
    public required string NormalizedEmail { get; set; }

    /// <summary>Full name shown in the UI and written to audit entries.</summary>
    public required string DisplayName { get; set; }

    /// <summary>Opaque password hash produced by <c>IPasswordHasher</c>.</summary>
    public required string PasswordHash { get; set; }

    /// <summary>Lifecycle state. Anything other than <see cref="UserStatus.Active"/> blocks sign-in.</summary>
    public UserStatus Status { get; set; } = UserStatus.Invited;

    /// <summary>
    /// Rotated whenever credentials or roles change. Existing refresh-token sessions carrying an
    /// older stamp are rejected, which is what makes "sign out everywhere" and role revocation take
    /// effect immediately rather than at the next access-token expiry.
    /// </summary>
    public Guid SecurityStamp { get; set; } = Guid.NewGuid();

    /// <summary>Whether the address has been verified.</summary>
    public bool EmailConfirmed { get; set; }

    /// <summary>Instant of the last successful sign-in, in UTC.</summary>
    public DateTimeOffset? LastLoginOn { get; set; }

    /// <summary>Consecutive failed sign-in attempts. Reset on success.</summary>
    public int FailedLoginAttempts { get; set; }

    /// <summary>
    /// How far this user has got through the product tour.
    /// </summary>
    /// <remarks>
    /// Columns on the user rather than a table of their own. It is a one-to-one relationship with
    /// three fields that are read together and written together, so a separate table would add a
    /// join to every read and a row-exists branch to every write, in exchange for nothing.
    /// <para>
    /// The default is <see cref="OnboardingStatus.NotStarted"/>, which is also the right answer for
    /// a user nobody has stored anything for - so "no state" needs no special handling anywhere.
    /// </para>
    /// </remarks>
    public OnboardingStatus OnboardingStatus { get; set; } = OnboardingStatus.NotStarted;

    /// <summary>
    /// Zero-based position an interrupted tour had reached.
    /// </summary>
    /// <remarks>
    /// An index rather than a step identifier, deliberately. The step list is derived from the
    /// user's own navigation and changes with their permissions and plan, so a stored identifier
    /// could name a step that no longer exists for them; an index is simply clamped to the new
    /// length.
    /// </remarks>
    public int OnboardingStepIndex { get; set; }

    /// <summary>Instant the tour state last changed.</summary>
    public DateTimeOffset? OnboardingUpdatedOn { get; set; }

    /// <summary>Instant the temporary lockout expires, in UTC.</summary>
    public DateTimeOffset? LockoutEndsOn { get; set; }

    /// <summary>Job title, shown on the employees screen.</summary>
    public string JobTitle { get; set; } = string.Empty;

    /// <summary>Avatar URL, surfaced in the token and the profile.</summary>
    public string? AvatarUrl { get; set; }

    /// <summary>Per-user adjustments to the permissions their role grants.</summary>
    public ICollection<UserPermissionOverride> PermissionOverrides { get; set; } = [];

    /// <summary>Owning tenant navigation.</summary>
    public Tenant? Tenant { get; set; }

    /// <summary>Roles assigned to this user.</summary>
    public ICollection<UserRole> UserRoles { get; set; } = [];

    /// <summary>Refresh-token sessions issued to this user.</summary>
    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
}
