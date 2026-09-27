using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Marketing.Application.Contracts;
using Marketing.Application.DTOs.Exports;
using Marketing.Application.Services.Exports;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Infrastructure.Exports;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.UnitTests.Services;

/// <summary>
/// Asking for an export.
/// </summary>
/// <remarks>
/// The property the whole design rests on is that this path reads none of the data being
/// exported. Several of these tests exist to hold that line: if creating an export ever starts
/// counting rows or opening a query, the endpoint stops being fast and the feature stops being
/// worth having.
/// </remarks>
public sealed class ExportJobTests
{
    private const long TenantId = 9701;
    private const long UserId = 97;
    private const long ColleagueId = 98;

    private static readonly DateTimeOffset Now = new(2026, 9, 27, 9, 0, 0, TimeSpan.Zero);

    private readonly List<ExportJob> _jobs = [];
    private readonly List<object> _published = [];

    private readonly IRepository<ExportJob> _repository = Substitute.For<IRepository<ExportJob>>();
    private readonly InMemoryQueryExecutor _queries = new();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IMessagePublisher _publisher = Substitute.For<IMessagePublisher>();
    private readonly IFileStorage _storage = Substitute.For<IFileStorage>();
    private readonly ICacheService _cache = Substitute.For<ICacheService>();
    private readonly StubExportDataset _dataset = new();

    private readonly StubCurrentUser _caller = new()
    {
        UserId = UserId,
        Roles = [Roles.Admin],
        Permissions = [Permissions.Contacts.View],
    };

    public ExportJobTests()
    {
        _repository.Query(Arg.Any<bool>()).Returns(_ => _jobs.Where(job => !job.IsDeleted).AsQueryable());

        _repository.When(repository => repository.Add(Arg.Any<ExportJob>()))
            .Do(call =>
            {
                var job = call.Arg<ExportJob>()!;

                // The database assigns the key on insert; the substitute stands in for that so
                // the returned public id is a real one.
                job.Id = _jobs.Count + 1;
                job.CreatedOn = Now;
                _jobs.Add(job);
            });

        _publisher.PublishAsync(Arg.Any<ExportJobQueued>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _published.Add(call.Arg<ExportJobQueued>()!);
                return Task.CompletedTask;
            });
    }

    private ExportJobService CreateService() =>
        new(
            _repository,
            _queries,
            _unitOfWork,
            _caller,
            new StubTenantContext { TenantId = TenantId },
            new ExportDatasetRegistry([_dataset]),
            [new CsvExportFileWriter()],
            _publisher,
            _storage,
            _cache,
            new FixedDateTimeProvider(Now),
            NullLogger<ExportJobService>.Instance);

    private static CreateExportRequest Request(
        string dataset = "contacts",
        ExportFormat format = ExportFormat.Csv,
        string? search = "john",
        IReadOnlyList<string>? columns = null) =>
        new(
            dataset,
            format,
            search,
            new Dictionary<string, string> { ["status"] = "Active", ["country"] = "US" },
            "createdAt",
            "desc",
            columns);

    [Fact]
    public async Task Asking_for_an_export_creates_a_queued_job()
    {
        var accepted = await CreateService().CreateAsync(Request(), TestContext.Current.CancellationToken);

        accepted.Status.Should().Be(ExportJobStatus.Queued);
        accepted.JobId.Should().StartWith("exj_");
        accepted.Reused.Should().BeFalse();

        var job = _jobs.Should().ContainSingle().Which;

        job.TenantId.Should().Be(TenantId);
        job.RequestedByUserId.Should().Be(UserId);
        job.Dataset.Should().Be("contacts");
        job.Status.Should().Be(ExportJobStatus.Queued);
    }

    [Fact]
    public async Task Asking_for_an_export_reads_none_of_the_data()
    {
        await CreateService().CreateAsync(Request(), TestContext.Current.CancellationToken);

        // The point of the feature. This request is a validation, an insert and a publish, so it
        // costs the same for ten rows as for a million - and the moment it counts or reads, the
        // endpoint is slow again for exactly the exports that needed it not to be.
        _dataset.Counted.Should().Be(0);
        _dataset.Read.Should().Be(0);
    }

    [Fact]
    public async Task The_message_carries_an_identifier_and_nothing_else()
    {
        await CreateService().CreateAsync(Request(), TestContext.Current.CancellationToken);

        var message = _published.Should().ContainSingle().Which.Should().BeOfType<ExportJobQueued>().Which;

        // Two numbers. No filters, no rows, no file - the worker reads the job row, which cannot
        // disagree with itself the way a copy in a message can.
        message.ExportJobId.Should().Be(_jobs[0].Id);
        message.TenantId.Should().Be(TenantId);
    }

    [Fact]
    public async Task The_job_is_committed_before_the_message_is_published()
    {
        await CreateService().CreateAsync(Request(), TestContext.Current.CancellationToken);

        // The other order publishes an identifier that does not exist yet, and a fast worker
        // reads nothing and fails a job the user can see in their history.
        Received.InOrder(() =>
        {
            _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>());
            _publisher.PublishAsync(Arg.Any<ExportJobQueued>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task The_list_views_state_is_captured_exactly_as_it_was()
    {
        await CreateService().CreateAsync(Request(), TestContext.Current.CancellationToken);

        var stored = JsonSerializer.Deserialize<ExportQuery>(_jobs[0].QueryJson, ExportQuery.SerializerOptions)!;

        // Not "export everything because they clicked Export". The filters could change, or a
        // contact could be edited, between the click and the worker starting.
        stored.Search.Should().Be("john");
        stored.Filter("status").Should().Be("Active");
        stored.Filter("country").Should().Be("US");
        stored.SortBy.Should().Be("createdAt");
        stored.Descending.Should().BeTrue();
    }

    [Fact]
    public async Task Naming_no_columns_takes_the_datasets_defaults()
    {
        await CreateService().CreateAsync(Request(columns: null), TestContext.Current.CancellationToken);

        _jobs[0].Columns.Should().BeEquivalentTo(["id", "fullName"]);
    }

    [Fact]
    public async Task Columns_are_written_in_the_order_they_were_asked_for()
    {
        await CreateService().CreateAsync(
            Request(columns: ["fullName", "id"]), TestContext.Current.CancellationToken);

        _jobs[0].Columns.Should().Equal("fullName", "id");
    }

    [Fact]
    public async Task A_column_that_is_not_on_the_allow_list_is_refused()
    {
        var call = async () => await CreateService().CreateAsync(
            Request(columns: ["fullName", "passwordHash"]), TestContext.Current.CancellationToken);

        // The whole column-security story in one test. A client that could name a column could
        // name a navigation or an expression; nothing reaches a query that is not in the
        // dataset's own catalogue.
        (await call.Should().ThrowAsync<ValidationException>())
            .Which.Errors["columns"][0].Should().Contain("not a column");

        _jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_dataset_is_a_404()
    {
        var call = async () => await CreateService().CreateAsync(
            Request(dataset: "salaries"), TestContext.Current.CancellationToken);

        await call.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task A_caller_without_the_lists_permission_cannot_export_it()
    {
        _caller.Permissions = [];

        var call = async () => await CreateService().CreateAsync(Request(), TestContext.Current.CancellationToken);

        // The same permission the list itself needs. An export is a read of the whole list, so
        // anything weaker would hand the data to somebody who cannot open the screen.
        await call.Should().ThrowAsync<ForbiddenException>();
        _jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task The_dataset_list_hides_what_the_caller_cannot_read()
    {
        _caller.Permissions = [];

        CreateService().Datasets().Should().BeEmpty();

        _caller.Permissions = [Permissions.Contacts.View];

        CreateService().Datasets().Should().ContainSingle().Which.Key.Should().Be("contacts");
    }

    [Fact]
    public async Task Clicking_export_five_times_starts_one_export()
    {
        var service = CreateService();

        var first = await service.CreateAsync(Request(), TestContext.Current.CancellationToken);

        for (var index = 0; index < 4; index++)
        {
            var again = await service.CreateAsync(Request(), TestContext.Current.CancellationToken);

            again.JobId.Should().Be(first.JobId);
            again.Reused.Should().BeTrue();
        }

        // One job, one message. The user is told their export is already running rather than
        // having their request silently dropped or five files produced.
        _jobs.Should().ContainSingle();
        _published.Should().ContainSingle();
    }

    [Fact]
    public async Task A_different_filter_is_a_different_export()
    {
        var service = CreateService();

        await service.CreateAsync(Request(search: "john"), TestContext.Current.CancellationToken);
        await service.CreateAsync(Request(search: "jane"), TestContext.Current.CancellationToken);

        // The guard is against a repeated click, not against exporting twice. Two different
        // views are two different questions.
        _jobs.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_colleagues_export_is_not_mine_to_see_or_download()
    {
        await CreateService().CreateAsync(Request(), TestContext.Current.CancellationToken);

        var id = PublicId.From(PublicId.ExportJob, _jobs[0].Id);

        _caller.UserId = ColleagueId;

        var service = CreateService();

        var read = async () => await service.GetAsync(id, TestContext.Current.CancellationToken);
        var download = async () => await service.OpenAsync(id, TestContext.Current.CancellationToken);

        // A 404, not a 403. An export is one person's extract, and a refusal that distinguished
        // "not yours" from "no such export" would confirm the identifier names something real.
        await read.Should().ThrowAsync<NotFoundException>();
        await download.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task The_history_shows_only_the_callers_own_exports()
    {
        await CreateService().CreateAsync(Request(), TestContext.Current.CancellationToken);

        _jobs.Add(new ExportJob
        {
            Id = 99,
            TenantId = TenantId,
            RequestedByUserId = ColleagueId,
            Dataset = "contacts",
            QueryJson = "{}",
            Fingerprint = "other",
            CreatedOn = Now,
        });

        var page = await CreateService().GetPageAsync(1, 20, TestContext.Current.CancellationToken);

        page.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task An_export_that_has_not_finished_cannot_be_downloaded()
    {
        await CreateService().CreateAsync(Request(), TestContext.Current.CancellationToken);

        var call = async () => await CreateService().OpenAsync(
            PublicId.From(PublicId.ExportJob, _jobs[0].Id), TestContext.Current.CancellationToken);

        (await call.Should().ThrowAsync<BusinessRuleException>())
            .Which.ErrorCode.Should().Be("export_not_ready");
    }

    [Fact]
    public async Task A_finished_export_is_downloaded_through_the_api_not_by_path()
    {
        var job = Completed();

        _storage.ExistsAsync("key", Arg.Any<CancellationToken>()).Returns(true);
        _storage.OpenAsync("key", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("a,b\n1,2\n"))));

        var download = await CreateService().OpenAsync(
            PublicId.From(PublicId.ExportJob, job.Id), TestContext.Current.CancellationToken);

        download.FileName.Should().Be("contacts-2026-09-27.csv");
        download.ContentType.Should().Be(AppConstants.ContentTypes.Csv);

        // The storage key is resolved here and never reaches the client, which only ever holds
        // the job id.
        await _storage.Received(1).OpenAsync("key", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_expired_export_cannot_be_downloaded_even_before_the_sweeper_runs()
    {
        var job = Completed();

        job.ExpiresAt = Now.AddMinutes(-1);

        var call = async () => await CreateService().OpenAsync(
            PublicId.From(PublicId.ExportJob, job.Id), TestContext.Current.CancellationToken);

        // Checked on read as well as by the cleanup job. A file that expires between two sweeps
        // must not be downloadable in the gap.
        (await call.Should().ThrowAsync<BusinessRuleException>())
            .Which.ErrorCode.Should().Be("export_expired");
    }

    [Fact]
    public async Task An_export_whose_file_has_vanished_says_so_rather_than_failing()
    {
        var job = Completed();

        _storage.ExistsAsync("key", Arg.Any<CancellationToken>()).Returns(false);

        var call = async () => await CreateService().OpenAsync(
            PublicId.From(PublicId.ExportJob, job.Id), TestContext.Current.CancellationToken);

        // A cleanup that ran early, a restored backup. An unhandled storage exception here would
        // be a 500 on a link the user was invited to click.
        (await call.Should().ThrowAsync<BusinessRuleException>())
            .Which.ErrorCode.Should().Be("export_expired");
    }

    [Fact]
    public async Task A_failed_export_can_be_retried_and_goes_back_to_the_queue()
    {
        var job = Completed();

        job.Status = ExportJobStatus.Failed;
        job.ErrorMessage = "We could not finish this export.";

        var accepted = await CreateService().RetryAsync(
            PublicId.From(PublicId.ExportJob, job.Id), TestContext.Current.CancellationToken);

        accepted.Status.Should().Be(ExportJobStatus.Queued);

        job.Status.Should().Be(ExportJobStatus.Queued);
        job.ErrorMessage.Should().BeNull();
        job.ProcessedRecords.Should().Be(0);

        _published.Should().ContainSingle();
    }

    [Fact]
    public async Task A_completed_export_cannot_be_retried()
    {
        var job = Completed();

        var call = async () => await CreateService().RetryAsync(
            PublicId.From(PublicId.ExportJob, job.Id), TestContext.Current.CancellationToken);

        // Retrying a finished export would write a second file over the first.
        await call.Should().ThrowAsync<BusinessRuleException>();
    }

    [Fact]
    public async Task A_running_export_can_be_withdrawn()
    {
        await CreateService().CreateAsync(Request(), TestContext.Current.CancellationToken);

        var response = await CreateService().CancelAsync(
            PublicId.From(PublicId.ExportJob, _jobs[0].Id), TestContext.Current.CancellationToken);

        response.Status.Should().Be(ExportJobStatus.Cancelled);
    }

    /// <summary>A completed export belonging to the caller, with a file behind it.</summary>
    private ExportJob Completed()
    {
        var job = new ExportJob
        {
            Id = 1,
            TenantId = TenantId,
            RequestedByUserId = UserId,
            Dataset = "contacts",
            Format = ExportFormat.Csv,
            Status = ExportJobStatus.Completed,
            QueryJson = "{}",
            Fingerprint = "f",
            FileName = "contacts-2026-09-27.csv",
            FileStorageKey = "key",
            FileSizeBytes = 8,
            TotalRecords = 2,
            ProcessedRecords = 2,
            CreatedOn = Now,
            CompletedAt = Now,
            ExpiresAt = Now.AddDays(7),
        };

        _jobs.Add(job);

        return job;
    }

    /// <summary>A dataset that counts how often it is asked to do work.</summary>
    private sealed class StubExportDataset : IExportDataset
    {
        public int Counted { get; private set; }

        public int Read { get; private set; }

        public string Key => "contacts";

        public string DisplayName => "Contacts";

        public string Permission => Permissions.Contacts.View;

        public IReadOnlyList<ExportColumn> Columns { get; } =
        [
            new("id", "ID"),
            new("fullName", "Name"),
            new("secret", "Secret", Default: false),
        ];

        public Task<int?> CountAsync(ExportQuery query, CancellationToken cancellationToken = default)
        {
            Counted++;

            return Task.FromResult<int?>(2);
        }

        public async IAsyncEnumerable<object?[]> ReadAsync(
            ExportQuery query,
            IReadOnlyList<ExportColumn> columns,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Read++;

            yield return ["cnt_1", "Ayesha Khan"];
            yield return ["cnt_2", "Bilal Ahmed"];

            await Task.CompletedTask;
        }
    }
}
