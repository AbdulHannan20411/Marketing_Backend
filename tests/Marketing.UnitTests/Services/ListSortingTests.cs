using System.Linq.Expressions;
using AwesomeAssertions;
using Marketing.Business.Extensions;
using Marketing.Common.Exceptions;
using Marketing.Common.Requests;
using Marketing.DataAccess.Entities;
using Microsoft.EntityFrameworkCore;
using static Marketing.Common.Constants.AppConstants;

namespace Marketing.UnitTests.Services;

/// <summary>
/// The sorting contract itself: the allow-list, the direction, and the tiebreak.
/// </summary>
public sealed class ApplySortTests
{
    private static readonly Dictionary<string, Expression<Func<Contact, object?>>> Allowed =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["fullName"] = contact => contact.FullName,
            ["status"] = contact => contact.Status,
        };

    private static IQueryable<Contact> Three() =>
        new[]
        {
            new Contact { Id = 3, FullName = "Cara", PhoneNumber = "+1", NormalizedPhoneNumber = "1", Country = "PK" },
            new Contact { Id = 1, FullName = "Ayesha", PhoneNumber = "+2", NormalizedPhoneNumber = "2", Country = "PK" },
            new Contact { Id = 2, FullName = "Bilal", PhoneNumber = "+3", NormalizedPhoneNumber = "3", Country = "PK" },
        }.AsQueryable();

    [Fact]
    public void A_field_outside_the_allow_list_is_refused_by_name()
    {
        var request = new PageRequest { SortBy = "passwordHash" };

        var call = () => Three().ApplySort(request, Allowed, contact => contact.Id).ToList();

        // Refused rather than ignored. A sort that silently does nothing is a list in the wrong
        // order under a header that claims otherwise, which nobody reports as a bug.
        //
        // The detail is in Errors, not in Message: ValidationException carries a per-field map so
        // the client can render it inline, and its Message is the generic heading.
        call.Should().Throw<ValidationException>()
            .Which.Errors[nameof(PageRequest.SortBy)][0]
            .Should().Contain("not a sortable field").And.Contain("fullName");
    }

    [Fact]
    public void The_allow_list_is_matched_without_regard_to_case()
    {
        var request = new PageRequest { SortBy = "FULLNAME" };

        Three().ApplySort(request, Allowed, contact => contact.Id)
            .Select(contact => contact.FullName).Should().Equal("Ayesha", "Bilal", "Cara");
    }

    [Fact]
    public void No_sort_falls_back_to_the_default_descending()
    {
        Three().ApplySort(new PageRequest(), Allowed, contact => contact.Id)
            .Select(contact => contact.Id).Should().Equal(3, 2, 1);
    }

    [Fact]
    public void The_result_can_be_given_a_tiebreak()
    {
        // The reason ApplySort returns an ordered query. Every allow-list in the app contains a
        // low-cardinality column, and LIMIT/OFFSET over a sort with ties is free to repeat a row
        // on one page and lose it from another.
        var ordered = Three()
            .ApplySort(new PageRequest { SortBy = "status" }, Allowed, contact => contact.Id)
            .ThenBy(contact => contact.Id);

        ordered.Select(contact => contact.Id).Should().Equal(1, 2, 3);
    }
}

/// <summary>
/// That <c>asc</c> and <c>desc</c> bind, and that a typo does not quietly become ascending.
/// </summary>
public sealed class SortDirectionBindingTests
{
    [Theory]
    [InlineData("asc", SortDirection.Ascending)]
    [InlineData("ascending", SortDirection.Ascending)]
    [InlineData("ASC", SortDirection.Ascending)]
    [InlineData("desc", SortDirection.Descending)]
    [InlineData("descending", SortDirection.Descending)]
    [InlineData("Descending", SortDirection.Descending)]
    [InlineData(" desc ", SortDirection.Descending)]
    public void Both_spellings_bind(string sent, SortDirection expected)
    {
        Convert(sent).Should().Be(expected);
    }

    [Fact]
    public void An_empty_direction_is_the_default_rather_than_an_error()
    {
        // A client clearing its sort sends sortDirection= and should not get a 400 for it.
        Convert(string.Empty).Should().Be(SortDirection.Ascending);
    }

    [Fact]
    public void A_direction_that_is_neither_fails_to_bind()
    {
        // The important half. Before the converter, "descnding" bound to nothing, and nothing is
        // Ascending - so the list came back in the opposite order to the arrow above it, with no
        // error anywhere. A binding failure is a 400 the client can see.
        var call = () => Convert("descnding");

        call.Should().Throw<FormatException>();
    }

    private static object? Convert(string value) =>
        new SortDirectionConverter().ConvertFrom(null, null, value);
}

/// <summary>
/// That each registered sort key is SQL the database will accept.
/// </summary>
/// <remarks>
/// Several of the keys are not plain columns - a coalesce, a counted subquery, a derived boolean,
/// a column on a left-joined table - and those are exactly the ones that compile in C# and throw
/// at runtime. The model is built against the real PostgreSQL provider and the SQL is requested
/// without opening a connection.
/// </remarks>
public sealed class SortableColumnSqlTests : IDisposable
{
    private readonly TestApplicationDbContext _context =
        TestDbContextFactory.Create(new StubTenantContext { TenantId = 9001 });

    public void Dispose() => _context.Dispose();

    private static string SortBy<TEntity>(
        IQueryable<TEntity> source,
        IReadOnlyDictionary<string, Expression<Func<TEntity, object?>>> allowed,
        string key,
        Expression<Func<TEntity, object?>> fallback)
        where TEntity : class =>
        source.ApplySort(new PageRequest { SortBy = key, SortDirection = SortDirection.Descending }, allowed, fallback)
            .Take(10)
            .ToQueryString();

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("status")]
    [InlineData("category")]
    [InlineData("updatedAt")]
    [InlineData("timesUsed")]
    public void Every_template_key_translates(string key)
    {
        var allowed = Sortable.Templates;

        var sql = SortBy(_context.Set<MessageTemplate>(), allowed, key, template => template.CreatedOn);

        sql.Should().Contain("ORDER BY");
    }

    [Fact]
    public void The_template_updated_column_sorts_by_the_value_it_prints()
    {
        var sql = SortBy(
            _context.Set<MessageTemplate>(), Sortable.Templates, "updatedAt", template => template.CreatedOn);

        // The response returns ModifiedOn ?? CreatedOn, so the sort has to be the same expression
        // or the column sorts by something the reader cannot see.
        sql.Should().Contain("COALESCE");
    }

    [Theory]
    [InlineData("name")]
    [InlineData("plan")]
    [InlineData("status")]
    [InlineData("seats")]
    [InlineData("messagesThisMonth")]
    [InlineData("createdAt")]
    public void Every_tenant_key_translates(string key)
    {
        var sql = SortBy(_context.Set<Tenant>(), Sortable.Tenants, key, tenant => tenant.Name);

        sql.Should().Contain("ORDER BY");
    }

    [Fact]
    public void Seats_is_counted_in_the_database_rather_than_over_the_page()
    {
        var sql = SortBy(_context.Set<Tenant>(), Sortable.Tenants, "seats", tenant => tenant.Name);

        // A count over the materialised page would sort ten rows by a number the other four
        // hundred also have. This is the same subquery the list projects.
        sql.Should().ContainEquivalentOf("count(");
    }

    [Theory]
    [InlineData("submittedAt")]
    [InlineData("reviewedAt")]
    [InlineData("status")]
    [InlineData("amount")]
    [InlineData("organisation")]
    public void Every_payment_key_translates(string key)
    {
        var sql = SortBy(_context.Set<PaymentRequest>(), Sortable.Payments, key, request => request.SubmittedAt);

        sql.Should().Contain("ORDER BY");
    }

    [Theory]
    [InlineData("occurredAt")]
    [InlineData("campaignName")]
    [InlineData("contactName")]
    [InlineData("errorCode")]
    public void Every_failure_key_translates(string key)
    {
        var sql = SortBy(_context.Set<DeliveryFailure>(), Sortable.Failures, key, failure => failure.OccurredOn);

        sql.Should().Contain("ORDER BY");
    }

    [Theory]
    [InlineData("occurrenceNumber")]
    [InlineData("scheduledFor")]
    [InlineData("startedAt")]
    [InlineData("completedAt")]
    [InlineData("status")]
    [InlineData("triggeredManually")]
    public void Every_campaign_run_key_translates(string key)
    {
        var sql = SortBy(_context.Set<CampaignRun>(), Sortable.Runs, key, run => run.ScheduledForUtc);

        sql.Should().Contain("ORDER BY");
    }

    /// <summary>
    /// The audit log's joined read, which is the one that is not a plain table scan.
    /// </summary>
    /// <remarks>
    /// Reproduces the query the service composes. The actor and the workspace are left-joined,
    /// because the system identity has no user row and a platform-level entry has no workspace -
    /// an inner join would silently drop exactly the entries a reviewer opens this screen to find.
    /// </remarks>
    private IQueryable<AuditRow> JoinedAudit() =>
        from entry in _context.Set<AuditLog>().AsNoTracking()
        join candidate in _context.Set<User>().AsNoTracking() on entry.UserId equals candidate.Id into candidates
        from actor in candidates.DefaultIfEmpty()
        join owner in _context.Set<Tenant>().AsNoTracking() on entry.TenantId equals (long?)owner.Id into owners
        from workspace in owners.DefaultIfEmpty()
        // An object initialiser, mirroring the service. Ordering happens after this projection,
        // and EF resolves a member access back through a member-init but not through a
        // constructor call - with positional arguments nothing here translated at all.
        select new AuditRow
        {
            Id = entry.Id,
            Action = entry.Action,
            OccurredOn = entry.OccurredOn,
            Actor = actor == null ? null : actor.DisplayName,
            Workspace = workspace == null ? null : workspace.Name,
        };

    private sealed record AuditRow
    {
        public long Id { get; init; }

        public AuditAction Action { get; init; }

        public DateTimeOffset OccurredOn { get; init; }

        public string? Actor { get; init; }

        public string? Workspace { get; init; }
    }

    [Fact]
    public void The_audit_read_left_joins_the_actor_and_the_workspace()
    {
        var sql = JoinedAudit().OrderByDescending(row => row.OccurredOn).Take(10).ToQueryString();

        sql.Should().Contain("LEFT JOIN");
        sql.Should().NotContain("INNER JOIN", "a background job's entry and a platform-level entry must survive");
    }

    [Theory]
    [InlineData("occurredAt")]
    [InlineData("actor")]
    [InlineData("action")]
    [InlineData("severity")]
    [InlineData("workspace")]
    public void Every_audit_key_translates(string key)
    {
        var allowed = new Dictionary<string, Expression<Func<AuditRow, object?>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["occurredAt"] = row => row.OccurredOn,
            ["actor"] = row => row.Actor,
            ["action"] = row => row.Action,
            ["severity"] = row => row.Action == AuditAction.Deleted,
            ["workspace"] = row => row.Workspace,
        };

        var sql = SortBy(JoinedAudit(), allowed, key, row => row.OccurredOn);

        sql.Should().Contain("ORDER BY");
    }

    /// <summary>
    /// Copies of the services' private allow-lists.
    /// </summary>
    /// <remarks>
    /// Duplicated rather than exposed: making a private static field internal so a test can read
    /// it widens the surface of five services to prove something about SQL. The risk this accepts
    /// is a key registered in a service and forgotten here, which shows up as a missing test
    /// rather than as a broken endpoint.
    /// </remarks>
    private static class Sortable
    {
        public static readonly Dictionary<string, Expression<Func<MessageTemplate, object?>>> Templates =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = template => template.Id,
                ["name"] = template => template.Name,
                ["status"] = template => template.Status,
                ["category"] = template => template.Category,
                ["updatedAt"] = template => template.ModifiedOn ?? template.CreatedOn,
                ["createdAt"] = template => template.CreatedOn,
                ["timesUsed"] = template => template.TimesUsed,
            };

        public static readonly Dictionary<string, Expression<Func<Tenant, object?>>> Tenants =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = tenant => tenant.Id,
                ["name"] = tenant => tenant.Name,
                ["plan"] = tenant => tenant.PlanBand,
                ["status"] = tenant => tenant.Status,
                ["seats"] = tenant => tenant.Users.Count(user => !user.IsDeleted),
                ["messagesThisMonth"] = tenant => tenant.MessagesThisMonth,
                ["createdAt"] = tenant => tenant.CreatedOn,
            };

        public static readonly Dictionary<string, Expression<Func<PaymentRequest, object?>>> Payments =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = request => request.Id,
                ["submittedAt"] = request => request.SubmittedAt,
                ["reviewedAt"] = request => request.ReviewedAt,
                ["status"] = request => request.Status,
                ["amount"] = request => request.Amount,
                ["organisation"] = request => request.Organisation,
                ["plan"] = request => request.PlanName,
            };

        public static readonly Dictionary<string, Expression<Func<CampaignRun, object?>>> Runs =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = run => run.Id,
                ["occurrenceNumber"] = run => run.OccurrenceNumber,
                ["scheduledFor"] = run => run.ScheduledForUtc,
                ["startedAt"] = run => run.StartedAt,
                ["completedAt"] = run => run.CompletedAt,
                ["status"] = run => run.Status,
                ["triggeredManually"] = run => run.TriggeredManually,
            };

        public static readonly Dictionary<string, Expression<Func<DeliveryFailure, object?>>> Failures =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = failure => failure.Id,
                ["occurredAt"] = failure => failure.OccurredOn,
                ["campaignName"] = failure => failure.CampaignName,
                ["contactName"] = failure => failure.ContactName,
                ["errorCode"] = failure => failure.ErrorCode,
                ["phoneNumber"] = failure => failure.PhoneNumber,
            };
    }
}
