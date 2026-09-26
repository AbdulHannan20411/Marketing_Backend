using AwesomeAssertions;
using Marketing.Application.DTOs.Contacts;
using Marketing.Application.Services;
using Marketing.Application.Services.Audit;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.DataAccess.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.UnitTests.Services;

/// <summary>
/// A contact's audit columns, and the names in them.
/// </summary>
/// <remarks>
/// An id in a "Modified by" column is the thing the record-history work was asked to stop doing,
/// so the names are the feature and the timestamps are the easy half.
/// </remarks>
public sealed class ContactAuditFieldTests
{
    private const long TenantId = 9501;
    private const long HoneyId = 71;
    private const long AyeshaId = 72;
    private const long DepartedId = 73;

    private static readonly DateTimeOffset Created = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Edited = new(2026, 9, 26, 9, 0, 0, TimeSpan.Zero);

    private readonly List<Contact> _contacts = [];
    private readonly List<User> _users = [];

    private readonly IRepository<Contact> _repository = Substitute.For<IRepository<Contact>>();
    private readonly IUserRepository _users_ = Substitute.For<IUserRepository>();
    private readonly InMemoryQueryExecutor _queries = new();

    public ContactAuditFieldTests()
    {
        _repository.Query(Arg.Any<bool>()).Returns(_ => _contacts.Where(row => !row.IsDeleted).AsQueryable());

        // IgnoreQueryFilters is what keeps a departed colleague's name readable, and over objects
        // it is a no-op - so the substitute returns everyone, deleted included, as the real query
        // does once filters are off.
        _users_.Query(Arg.Any<bool>()).Returns(_ => _users.AsQueryable());
    }

    private ContactService CreateService() =>
        new(
            _repository,
            Substitute.For<IRepository<ContactGroup>>(),
            Substitute.For<IRepository<ContactTag>>(),
            _queries,
            new ActorNames(_users_, _queries));

    private void Person(long id, string name, bool departed = false) =>
        _users.Add(new User
        {
            Id = id,
            TenantId = TenantId,
            Email = $"{id}@example.com",
            NormalizedEmail = $"{id}@EXAMPLE.COM",
            DisplayName = name,
            PasswordHash = "pbkdf2-sha256$1000$c2FsdA==$aGFzaA==",
            IsDeleted = departed,
        });

    private Contact Saved(long createdBy, long? modifiedBy = null)
    {
        var contact = new Contact
        {
            Id = _contacts.Count + 1,
            TenantId = TenantId,
            FullName = $"Contact {_contacts.Count + 1}",
            PhoneNumber = $"+9230000{_contacts.Count + 1:0000}",
            NormalizedPhoneNumber = $"9230000{_contacts.Count + 1:0000}",
            Country = "PK",
            Status = ContactStatus.Subscribed,
            CreatedBy = createdBy,
            CreatedOn = Created,
            ModifiedBy = modifiedBy,
            ModifiedOn = modifiedBy is null ? null : Edited,
        };

        _contacts.Add(contact);

        return contact;
    }

    private async Task<ContactResponse> FirstAsync()
    {
        var page = await CreateService().GetContactsAsync(
            new ContactQuery { Page = 1, PageSize = 20 }, TestContext.Current.CancellationToken);

        return page.Items.Should().ContainSingle().Which;
    }

    [Fact]
    public async Task A_contact_carries_who_created_it_and_when()
    {
        Person(HoneyId, "Honey");
        Saved(createdBy: HoneyId);

        var contact = await FirstAsync();

        contact.CreatedBy.Should().Be("Honey");
        contact.CreatedAt.Should().Be(Created);
    }

    [Fact]
    public async Task A_contact_that_has_been_edited_carries_who_and_when()
    {
        Person(HoneyId, "Honey");
        Person(AyeshaId, "Ayesha Khan");
        Saved(createdBy: HoneyId, modifiedBy: AyeshaId);

        var contact = await FirstAsync();

        contact.UpdatedBy.Should().Be("Ayesha Khan");
        contact.UpdatedAt.Should().Be(Edited);
    }

    [Fact]
    public async Task A_contact_nobody_has_edited_says_so_with_nulls()
    {
        Person(HoneyId, "Honey");
        Saved(createdBy: HoneyId);

        var contact = await FirstAsync();

        // The client renders an em dash. Null is the honest value: there is no edit, as opposed
        // to an edit by somebody unnameable.
        contact.UpdatedBy.Should().BeNull();
        contact.UpdatedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_colleague_who_has_left_keeps_their_name_on_what_they_did()
    {
        Person(DepartedId, "Sana Iqbal", departed: true);
        Saved(createdBy: DepartedId);

        // A column that reads "—" the day somebody leaves is worse than no column, and their name
        // is already written on every row they touched.
        (await FirstAsync()).CreatedBy.Should().Be("Sana Iqbal");
    }

    [Fact]
    public async Task A_row_written_by_the_platform_has_no_name()
    {
        // The system identity. Naming it tells a reader nothing, and the seeded and imported rows
        // are exactly the ones where "created by" is not a person.
        Saved(createdBy: Marketing.Common.Constants.AppConstants.Platform.SystemUserId);

        (await FirstAsync()).CreatedBy.Should().BeNull();
    }

    [Fact]
    public async Task Naming_a_page_costs_the_same_whether_it_holds_one_row_or_twenty()
    {
        Person(HoneyId, "Honey");
        Person(AyeshaId, "Ayesha Khan");

        Saved(createdBy: HoneyId, modifiedBy: AyeshaId);

        var before = _queries.Queries;

        await CreateService().GetContactsAsync(
            new ContactQuery { Page = 1, PageSize = 20 }, TestContext.Current.CancellationToken);

        var forOne = _queries.Queries - before;

        for (var index = 0; index < 19; index++)
        {
            Saved(createdBy: HoneyId, modifiedBy: AyeshaId);
        }

        before = _queries.Queries;

        var page = await CreateService().GetContactsAsync(
            new ContactQuery { Page = 1, PageSize = 20 }, TestContext.Current.CancellationToken);

        page.Items.Should().HaveCount(20);

        // One lookup for the distinct actors on the page, not a join per row. Asserted as "does
        // not grow" rather than as a number, because the number belongs to the executor rather
        // than to this behaviour.
        (_queries.Queries - before).Should().Be(forOne);
    }

    [Fact]
    public async Task An_unknown_actor_id_renders_as_nothing_rather_than_throwing()
    {
        Saved(createdBy: 999999);

        // A row can outlive the account that wrote it in ways a soft delete does not cover - a
        // hard-deleted seed user, a restored backup. The column is not worth an exception.
        (await FirstAsync()).CreatedBy.Should().BeNull();
    }
}

/// <summary>
/// What the entitlements endpoint says about a workspace that has never bought a plan.
/// </summary>
public sealed class EntitlementsWithoutAPlanTests : IDisposable
{
    private readonly MemoryCache _memory = new(new MemoryCacheOptions());
    private readonly InMemoryQueryExecutor _queries = new();

    public void Dispose() => _memory.Dispose();

    private BillingService CreateService() =>
        new(
            Empty<TenantSubscription>(),
            Empty<SubscriptionPlan>(),
            Empty<Invoice>(),
            Empty<Tenant>(),
            Empty<BillingProfile>(),
            Substitute.For<Marketing.Application.Services.Billing.IInvoiceRenderer>(),
            Options.Create(new Marketing.Application.Configurations.EmailOptions()),
            Empty<Payment>(),
            Empty<RenewalRecord>(),
            Empty<Contact>(),
            Empty<Campaign>(),
            Empty<MessageDailyStat>(),
            Empty<WhatsAppConnection>(),
            Empty<User>(),
            _queries,
            Substitute.For<IUnitOfWork>(),
            Substitute.For<Marketing.Application.Interfaces.IPaymentGateway>(),
            new StubTenantContext { TenantId = 9601 },
            new FixedDateTimeProvider(new DateTimeOffset(2026, 9, 26, 9, 0, 0, TimeSpan.Zero)),
            _memory);

    private static IRepository<TEntity> Empty<TEntity>()
        where TEntity : BaseEntity
    {
        var repository = Substitute.For<IRepository<TEntity>>();

        repository.Query(Arg.Any<bool>()).Returns(_ => Array.Empty<TEntity>().AsQueryable());

        return repository;
    }

    [Fact]
    public async Task A_workspace_with_no_plan_gets_a_404_and_not_an_empty_snapshot()
    {
        var call = async () => await CreateService().GetEntitlementsAsync(TestContext.Current.CancellationToken);

        var refusal = (await call.Should().ThrowAsync<RequestRejectedException>()).Which;

        // The client keeps this one status apart from every other failure: a 404 is an answer -
        // there is no plan, lock and grant nothing - while a 500 or a timeout fails open, because
        // locking a paying customer out over a lost request is worse. An empty 200 would blur the
        // two back together.
        refusal.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_404_names_its_reason_so_the_client_need_not_infer_it()
    {
        var call = async () => await CreateService().GetEntitlementsAsync(TestContext.Current.CancellationToken);

        var refusal = (await call.Should().ThrowAsync<RequestRejectedException>()).Which;

        // A bare 404 on this route would also be produced by a misspelled path or a version bump,
        // and those are "unknown", not "no plan" - which fail open and lock respectively.
        refusal.ErrorCode.Should().Be(BillingService.NoSubscriptionCode);
    }
}
