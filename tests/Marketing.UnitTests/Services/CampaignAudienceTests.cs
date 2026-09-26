using AwesomeAssertions;
using Marketing.Application.DTOs.Campaigns;
using Marketing.Application.Services;
using Marketing.Application.Services.Campaigns;
using Marketing.Business.Repositories.Implementations;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Helpers;
using Marketing.Common.Requests;
using Marketing.Common.Responses;
using Marketing.DataAccess.Entities;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.UnitTests.Services;

/// <summary>
/// The audience a campaign would reach: counted, and listed.
/// </summary>
/// <remarks>
/// These two answers describe one thing, and the screen shows them together - a total above a list.
/// Most of what follows is about keeping them from drifting apart, because the day they disagree is
/// the day an operator stops trusting the number they are about to be billed against.
/// </remarks>
public sealed class CampaignAudienceTests
{
    private const long TenantId = 8001;

    private readonly List<Contact> _contacts = [];
    private readonly List<ContactGroupMember> _memberships = [];

    private readonly IRepository<Contact> _contactRepository = Substitute.For<IRepository<Contact>>();
    private readonly IRepository<ContactGroupMember> _members = Substitute.For<IRepository<ContactGroupMember>>();
    private readonly InMemoryQueryExecutor _queries = new();

    public CampaignAudienceTests()
    {
        _contactRepository.Query(Arg.Any<bool>()).Returns(_ => _contacts.Where(row => !row.IsDeleted).AsQueryable());
        _members.Query(Arg.Any<bool>()).Returns(_ => _memberships.Where(row => !row.IsDeleted).AsQueryable());
    }

    private CampaignWriteService CreateService() =>
        new(
            Substitute.For<IRepository<Campaign>>(),
            Substitute.For<IRepository<CampaignRun>>(),
            Substitute.For<IRecurrenceCalculator>(),
            Substitute.For<IRepository<MessageTemplate>>(),
            Substitute.For<IWhatsAppConnectionRepository>(),
            _members,
            _contactRepository,
            _queries,
            Substitute.For<IUnitOfWork>(),
            Substitute.For<Marketing.Application.Interfaces.IRealtimeNotifier>(),
            new StubCurrentUser { UserId = 81 },
            new StubTenantContext { TenantId = TenantId },
            new FixedDateTimeProvider(new DateTimeOffset(2026, 9, 26, 9, 0, 0, TimeSpan.Zero)),
            Substitute.For<Marketing.Application.Services.WhatsApp.IWhatsAppAccessService>(),
            Substitute.For<ICampaignHeaderMedia>());

    private Contact Person(
        string name,
        ContactStatus status = ContactStatus.Subscribed,
        bool deleted = false,
        params long[] groups)
    {
        var contact = new Contact
        {
            Id = _contacts.Count + 1,
            TenantId = TenantId,
            FullName = name,
            PhoneNumber = $"+9230000{_contacts.Count + 1:0000}",
            NormalizedPhoneNumber = $"9230000{_contacts.Count + 1:0000}",
            Country = "PK",
            Status = status,
            IsDeleted = deleted,
        };

        _contacts.Add(contact);

        foreach (var group in groups)
        {
            _memberships.Add(new ContactGroupMember
            {
                Id = _memberships.Count + 1,
                TenantId = TenantId,
                ContactGroupId = group,
                ContactId = contact.Id,
                Contact = contact,
            });
        }

        return contact;
    }

    private static string Group(long id) => PublicId.From(PublicId.Group, id);

    private Task<PagedResult<AudienceRecipient>> ListAsync(
        IReadOnlyList<string>? groupIds,
        int page = 1,
        int pageSize = 8) =>
        CreateService().PreviewAudienceContactsAsync(
            new AudienceRecipientsRequest(groupIds, null),
            new PageRequest { Page = page, PageSize = pageSize },
            TestContext.Current.CancellationToken);

    private Task<PreviewAudienceResponse> CountAsync(IReadOnlyList<string> groupIds) =>
        CreateService().PreviewAudienceAsync(
            new PreviewAudienceRequest(groupIds), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Somebody_in_two_chosen_groups_is_one_recipient()
    {
        Person("Ayesha Khan", groups: [1, 2]);
        Person("Bilal Ahmed", groups: [1]);
        Person("Sana Iqbal", groups: [2]);

        var page = await ListAsync([Group(1), Group(2)]);

        // Three names, not four. The list is what the operator is about to send to, and a name
        // appearing twice is also a number counted twice on the invoice.
        page.TotalItems.Should().Be(3);
        page.Items.Select(row => row.FullName)
            .Should().BeEquivalentTo(["Ayesha Khan", "Bilal Ahmed", "Sana Iqbal"]);
    }

    [Fact]
    public async Task The_total_above_the_list_is_the_same_number_the_count_returns()
    {
        Person("Ayesha Khan", groups: [1, 2]);
        Person("Bilal Ahmed", groups: [1]);
        Person("Sana Iqbal", status: ContactStatus.Unsubscribed, groups: [2]);
        Person("Omar Farooq", deleted: true, groups: [1]);

        var groups = new[] { Group(1), Group(2) };

        var listed = await ListAsync(groups);
        var counted = await CountAsync(groups);

        // The point of the whole exercise. Both are the same query - one counted, one paged - so
        // this cannot be made to fail without changing the definition of an audience.
        listed.TotalItems.Should().Be(counted.RecipientCount);
        listed.TotalItems.Should().Be(2);
    }

    [Fact]
    public async Task Somebody_who_has_unsubscribed_is_not_in_the_list()
    {
        Person("Ayesha Khan", groups: [1]);
        Person("Bilal Ahmed", status: ContactStatus.Unsubscribed, groups: [1]);
        Person("Sana Iqbal", status: ContactStatus.Blocked, groups: [1]);

        var page = await ListAsync([Group(1)]);

        // Excluded here rather than at send time, for the same reason the count excludes them:
        // listing somebody the dispatcher will refuse to message is a promise nothing keeps.
        page.Items.Should().ContainSingle().Which.FullName.Should().Be("Ayesha Khan");
        page.Items.Should().OnlyContain(row => row.Status == ContactStatus.Subscribed);
    }

    [Fact]
    public async Task A_deleted_contact_is_not_in_the_list()
    {
        Person("Ayesha Khan", groups: [1]);
        Person("Bilal Ahmed", deleted: true, groups: [1]);

        (await ListAsync([Group(1)])).TotalItems.Should().Be(1);
    }

    [Fact]
    public async Task A_contact_in_a_group_nobody_chose_is_not_in_the_list()
    {
        Person("Ayesha Khan", groups: [1]);
        Person("Bilal Ahmed", groups: [9]);

        var page = await ListAsync([Group(1)]);

        page.Items.Should().ContainSingle().Which.FullName.Should().Be("Ayesha Khan");
    }

    [Fact]
    public async Task The_order_is_by_name_and_does_not_shift_between_pages()
    {
        Person("Zara Malik", groups: [1]);
        Person("Ayesha Khan", groups: [1]);
        Person("Ayesha Khan", groups: [1]);
        Person("Bilal Ahmed", groups: [1]);

        var first = await ListAsync([Group(1)], page: 1, pageSize: 2);
        var second = await ListAsync([Group(1)], page: 2, pageSize: 2);

        first.Items.Select(row => row.FullName).Should().Equal("Ayesha Khan", "Ayesha Khan");
        second.Items.Select(row => row.FullName).Should().Equal("Bilal Ahmed", "Zara Malik");

        // The reason for the tiebreak: two people with the same name sort arbitrarily otherwise,
        // and the arbitrary part is free to differ between the query for page one and the query
        // for page two - which shows one of them twice and loses the other entirely.
        first.Items.Concat(second.Items).Select(row => row.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Paging_reports_the_whole_audience_rather_than_the_page()
    {
        for (var index = 0; index < 20; index++)
        {
            Person($"Person {index:00}", groups: [1]);
        }

        var page = await ListAsync([Group(1)], page: 2, pageSize: 8);

        page.Items.Should().HaveCount(8);
        page.TotalItems.Should().Be(20);
        page.TotalPages.Should().Be(3);
        page.Page.Should().Be(2);
    }

    [Fact]
    public async Task No_groups_is_an_empty_audience_rather_than_a_refusal()
    {
        Person("Ayesha Khan", groups: [1]);

        var empty = await ListAsync([]);
        var missing = await ListAsync(null);

        // Matching the count, which answers zero for the same input. The wizard asks both
        // questions before anything has been chosen, and a 400 on the first render is noise.
        empty.TotalItems.Should().Be(0);
        empty.Items.Should().BeEmpty();
        missing.TotalItems.Should().Be(0);

        (await CountAsync([])).RecipientCount.Should().Be(0);
    }

    [Fact]
    public async Task An_unreadable_group_identifier_is_dropped_rather_than_matched()
    {
        Person("Ayesha Khan", groups: [1]);

        var page = await ListAsync([Group(1), "tag_4", "nonsense"]);

        // The same parsing the count has always done, so a client sending a stale identifier gets
        // the same answer from both.
        page.TotalItems.Should().Be(1);
    }

    [Fact]
    public async Task Every_recipient_carries_the_initials_the_rest_of_the_app_shows()
    {
        Person("Ayesha Khan", groups: [1]);

        var recipient = (await ListAsync([Group(1)])).Items.Should().ContainSingle().Which;

        // Derived server-side so this dialog's avatars match the contacts list's. Two
        // implementations disagree about a three-part name eventually.
        recipient.Initials.Should().Be(Initials.From("Ayesha Khan"));
        recipient.Id.Should().StartWith("cnt_");
        recipient.PhoneNumber.Should().NotBeEmpty();
    }
}

/// <summary>
/// That the audience query is SQL the database will accept.
/// </summary>
/// <remarks>
/// The list is a subquery over the count's own query, which is the whole point of it - but a
/// captured <c>IQueryable</c> used inside a <c>Contains</c> is exactly the shape that translates in
/// theory and throws at runtime. The substituted repositories the tests above use run in memory and
/// would never notice. This builds the model against the real PostgreSQL provider and asks for the
/// SQL without opening a connection.
/// </remarks>
public sealed class CampaignAudienceSqlTests : IDisposable
{
    private readonly TestApplicationDbContext _context =
        TestDbContextFactory.Create(new StubTenantContext { TenantId = 8001 });

    public void Dispose() => _context.Dispose();

    /// <summary>Captures the SQL of the paged query instead of running it.</summary>
    private sealed class SqlCapturingQueryExecutor : IQueryExecutor
    {
        public string? Sql { get; private set; }

        public Task<PagedResult<TResult>> ToPagedAsync<TResult>(
            IQueryable<TResult> query, int page, int pageSize, CancellationToken cancellationToken = default)
        {
            Sql = query.Skip((page - 1) * pageSize).Take(pageSize).ToQueryString();

            return Task.FromResult(new PagedResult<TResult>([], 0, page, pageSize));
        }

        public Task<IReadOnlyList<TResult>> ToListAsync<TResult>(IQueryable<TResult> query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TResult?> FirstOrDefaultAsync<TResult>(IQueryable<TResult> query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> CountAsync<TResult>(IQueryable<TResult> query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> SumAsync(IQueryable<int> query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResult> StreamAsync<TResult>(IQueryable<TResult> query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task The_audience_list_translates_to_sql()
    {
        var capture = new SqlCapturingQueryExecutor();

        var service = new CampaignWriteService(
            Substitute.For<IRepository<Campaign>>(),
            Substitute.For<IRepository<CampaignRun>>(),
            Substitute.For<IRecurrenceCalculator>(),
            Substitute.For<IRepository<MessageTemplate>>(),
            Substitute.For<IWhatsAppConnectionRepository>(),
            new Repository<ContactGroupMember>(_context),
            new Repository<Contact>(_context),
            capture,
            Substitute.For<IUnitOfWork>(),
            Substitute.For<Marketing.Application.Interfaces.IRealtimeNotifier>(),
            new StubCurrentUser { UserId = 81 },
            new StubTenantContext { TenantId = 8001 },
            new FixedDateTimeProvider(new DateTimeOffset(2026, 9, 26, 9, 0, 0, TimeSpan.Zero)),
            Substitute.For<Marketing.Application.Services.WhatsApp.IWhatsAppAccessService>(),
            Substitute.For<ICampaignHeaderMedia>());

        await service.PreviewAudienceContactsAsync(
            new AudienceRecipientsRequest([PublicId.From(PublicId.Group, 1)], "ayesha"),
            new PageRequest { Page = 2, PageSize = 8 },
            TestContext.Current.CancellationToken);

        var sql = capture.Sql.Should().NotBeNull().And.Subject.ToString()!;

        // The audience arrives as a subquery over group membership - one predicate, shared with
        // the count - rather than as a second reading of what a group contains.
        sql.Should().Contain("contact_group_members");
        sql.Should().Contain("c.id IN (");

        // Search and paging both happen in the database. Either one done in memory would mean
        // fetching the whole audience to show eight rows of it.
        sql.Should().Contain("ILIKE @term");
        sql.Should().Contain("ORDER BY c.full_name, c.id");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("OFFSET");

        // The term is a bound parameter, never concatenated in. It is user input reaching a LIKE
        // pattern, so it is worth pinning rather than assuming. (ToQueryString prints parameter
        // values in a comment header, so the statement itself is what is checked.)
        var statement = sql[sql.IndexOf("SELECT", StringComparison.Ordinal)..];

        statement.Should().NotContain("ayesha");
    }
}
