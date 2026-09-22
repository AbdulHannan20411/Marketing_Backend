using System.Text.Json;
using AwesomeAssertions;
using Marketing.DataAccess.Entities;
using Marketing.DataAccess.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using static Marketing.Common.Constants.AppConstants;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.UnitTests.Interceptors;

/// <summary>
/// What the audit trail records, and what it refuses to.
/// </summary>
/// <remarks>
/// The history panel is only as honest as this interceptor: a field it fails to notice never
/// appears, and a field it records too eagerly - a password hash, a message body - turns the audit
/// table into a second copy of the thing it was protecting.
/// </remarks>
public sealed class AuditTrailInterceptorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 11, 45, 0, TimeSpan.Zero);
    private const long ActingUserId = 1001;
    private const long TenantId = 2001;

    private readonly StubTenantContext _tenantContext = new() { TenantId = TenantId };
    private readonly StubCurrentUser _currentUser = new() { UserId = ActingUserId };
    private readonly TestApplicationDbContext _context;
    private readonly AuditTrailInterceptor _interceptor;

    public AuditTrailInterceptorTests()
    {
        _context = TestDbContextFactory.Create(_tenantContext);
        _interceptor = new AuditTrailInterceptor(
            _currentUser,
            new StubRequestContext(),
            new FixedDateTimeProvider(Now));
    }

    public void Dispose() => _context.Dispose();

    private void Intercept() =>
        _interceptor.SavingChanges(new DbContextEventData(null!, null!, _context), default);

    private IReadOnlyList<AuditLog> Written() =>
        [.. _context.ChangeTracker.Entries<AuditLog>().Select(entry => entry.Entity)];

    private static JsonElement Changes(AuditLog log) =>
        JsonSerializer.Deserialize<JsonElement>(log.Changes ?? "{}");

    private static User NewUser() => new()
    {
        Email = "operator@example.com",
        NormalizedEmail = "OPERATOR@EXAMPLE.COM",
        DisplayName = "Operator",
        PasswordHash = "pbkdf2-sha256$1000$c2FsdA==$aGFzaA==",
        JobTitle = "Manager",
    };

    private User Existing()
    {
        var user = NewUser();

        user.Id = 5001;
        user.TenantId = TenantId;

        _context.Attach(user);

        return user;
    }

    [Fact]
    public void A_create_records_what_was_set_and_who_set_it()
    {
        _context.Users.Add(NewUser());

        Intercept();

        var log = Written().Should().ContainSingle().Which;

        log.Action.Should().Be(AuditAction.Created);
        log.UserId.Should().Be(ActingUserId);
        log.EntityName.Should().Be(nameof(User));
        log.OccurredOn.Should().Be(Now);
        log.OccurredOn.Offset.Should().Be(TimeSpan.Zero, "an audit trail is kept in UTC");

        var changes = Changes(log);

        changes.GetProperty("DisplayName").GetProperty("new").GetString().Should().Be("Operator");

        // A create has nothing to compare against, so it reports new values alone.
        changes.GetProperty("DisplayName").TryGetProperty("old", out _).Should().BeFalse();
    }

    [Fact]
    public void An_update_records_the_two_fields_that_moved_and_not_the_eighteen_that_did_not()
    {
        var user = Existing();

        user.DisplayName = "Operator Two";
        user.JobTitle = "Director";

        Intercept();

        var changes = Changes(Written().Should().ContainSingle().Which);

        changes.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo([nameof(User.DisplayName), nameof(User.JobTitle)]);

        changes.GetProperty(nameof(User.DisplayName)).GetProperty("old").GetString().Should().Be("Operator");
        changes.GetProperty(nameof(User.DisplayName)).GetProperty("new").GetString().Should().Be("Operator Two");
    }

    [Fact]
    public void An_entity_saved_without_a_single_change_is_not_recorded_at_all()
    {
        Existing();

        Intercept();

        // "Somebody pressed Save" is not history. An entry per no-op would bury the changes that
        // matter under the ones that did nothing.
        Written().Should().BeEmpty();
    }

    [Fact]
    public void A_soft_delete_is_recorded_as_a_delete_rather_than_as_a_flag_that_moved()
    {
        var user = Existing();

        user.IsDeleted = true;

        Intercept();

        Written().Should().ContainSingle().Which.Action.Should().Be(AuditAction.Deleted);
    }

    [Fact]
    public void A_hard_delete_is_recorded_with_the_values_it_took_away()
    {
        var user = Existing();

        _context.Users.Remove(user);

        Intercept();

        var log = Written().Should().ContainSingle().Which;

        log.Action.Should().Be(AuditAction.Deleted);

        var changes = Changes(log);

        // The row is going: the old values are the entry's whole point, and there is no new value
        // to report. Before this, a delete that skipped the soft-delete rewrite left no trace.
        changes.GetProperty(nameof(User.DisplayName)).GetProperty("old").GetString().Should().Be("Operator");
        changes.GetProperty(nameof(User.DisplayName)).TryGetProperty("new", out _).Should().BeFalse();
    }

    [Fact]
    public void An_enum_is_recorded_as_the_name_the_rest_of_the_api_uses()
    {
        var tag = new ContactTag { Id = 9, TenantId = TenantId, Name = "slow", Color = TagColor.Danger };

        _context.Attach(tag);

        tag.Color = TagColor.Info;

        Intercept();

        var changes = Changes(Written().Should().ContainSingle().Which);

        // "4" and "3" told a reader nothing, and the mapping lives in a C# enum they cannot see.
        // POST /tags takes {"color": "danger"}, so the history says the same word.
        changes.GetProperty(nameof(ContactTag.Color)).GetProperty("old").GetString().Should().Be("danger");
        changes.GetProperty(nameof(ContactTag.Color)).GetProperty("new").GetString().Should().Be("info");
    }

    [Fact]
    public void Bookkeeping_columns_are_not_recorded_as_fields()
    {
        var user = Existing();

        user.DisplayName = "Operator Two";
        user.ModifiedBy = 77;
        user.ModifiedOn = Now;

        Intercept();

        var changes = Changes(Written().Should().ContainSingle().Which);

        // Who and when are the audit row's own columns, and the panel puts them in the entry's
        // heading. Recorded as fields as well, one rename read as "3 fields changed".
        changes.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo([nameof(User.DisplayName)]);
    }

    [Fact]
    public void A_soft_delete_survives_its_change_set_being_empty()
    {
        var user = Existing();

        // A soft delete sets only bookkeeping columns, and none of those are recorded as fields.
        // Dropping the row for having no changes would lose the delete itself.
        user.IsDeleted = true;
        user.DeletedOn = Now;
        user.DeletedBy = ActingUserId;

        Intercept();

        var log = Written().Should().ContainSingle().Which;

        log.Action.Should().Be(AuditAction.Deleted);

        // Empty, and kept: the delete is the entry, and its own columns say who and when.
        log.Changes.Should().Be("{}");
    }

    [Fact]
    public void A_password_hash_is_recorded_as_redacted_and_appears_nowhere()
    {
        var user = Existing();

        user.PasswordHash = "pbkdf2-sha256$1000$bmV3$c2VjcmV0";

        Intercept();

        var log = Written().Should().ContainSingle().Which;

        Changes(log).GetProperty(nameof(User.PasswordHash)).GetProperty("redacted").GetBoolean()
            .Should().BeTrue();

        // The change is recorded; the secret is not. An audit table holding password hashes is a
        // second credential store with weaker guards around it.
        log.Changes.Should().NotContain("bmV3");
        log.Changes.Should().NotContain("pbkdf2");
    }

    [Fact]
    public void An_excluded_entity_leaves_no_trail()
    {
        _context.Set<RefreshToken>().Add(new RefreshToken
        {
            UserId = 5001,
            TenantId = TenantId,
            TokenHash = "hashed",
            SessionId = Guid.NewGuid(),
            ExpiresOn = Now.AddDays(7),
        });

        Intercept();

        Written().Should().BeEmpty();
    }

    [Fact]
    public void A_change_with_nobody_signed_in_is_attributed_to_the_platform()
    {
        _currentUser.UserId = null;

        _context.Users.Add(NewUser());

        Intercept();

        // The endpoint turns this into userId: null, and the client shows "Automatic".
        Written().Should().ContainSingle().Which.UserId.Should().Be(Platform.SystemUserId);
    }
}
