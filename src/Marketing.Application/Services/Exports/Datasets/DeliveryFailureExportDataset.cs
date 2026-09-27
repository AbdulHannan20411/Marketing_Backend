using System.Linq.Expressions;
using Marketing.Business.Extensions;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Helpers;
using Marketing.Common.Requests;
using Marketing.DataAccess.Entities;

namespace Marketing.Application.Services.Exports.Datasets;

/// <summary>
/// Exports the delivery failure log.
/// </summary>
/// <remarks>
/// The second dataset, and mostly here to show what adding one costs: a column catalogue, a
/// filter translation and a projection. Everything else - the job row, the queue, the writers,
/// storage, the notification, expiry, the download's authorisation - is shared and untouched.
/// <para>
/// This list already had a synchronous CSV export. That endpoint still works and is still the
/// right answer for a few hundred rows; this is what the screen offers when the log is large
/// enough that a request would time out waiting for it.
/// </para>
/// </remarks>
public sealed class DeliveryFailureExportDataset : IExportDataset
{
    /// <summary>
    /// Sortable columns, matched against the captured <c>sortBy</c>.
    /// </summary>
    /// <remarks>
    /// The same keys the paged endpoint accepts, so a view sorted by error code exports sorted by
    /// error code. An allow-list here as there, because the value arrives from a client either way.
    /// </remarks>
    private static readonly Dictionary<string, Expression<Func<DeliveryFailure, object?>>> SortableColumns =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = failure => failure.Id,
            ["occurredAt"] = failure => failure.OccurredOn,
            ["campaignName"] = failure => failure.CampaignName,
            ["contactName"] = failure => failure.ContactName,
            ["errorCode"] = failure => failure.ErrorCode,
            ["phoneNumber"] = failure => failure.PhoneNumber,
        };

    private readonly IRepository<DeliveryFailure> _failures;
    private readonly IQueryExecutor _queries;

    /// <summary>Initialises a new instance.</summary>
    public DeliveryFailureExportDataset(IRepository<DeliveryFailure> failures, IQueryExecutor queries)
    {
        _failures = failures;
        _queries = queries;
    }

    /// <inheritdoc />
    public string Key => "failures";

    /// <inheritdoc />
    public string DisplayName => "Delivery failures";

    /// <inheritdoc />
    public string Permission => Permissions.Reports.Export;

    /// <inheritdoc />
    public IReadOnlyList<ExportColumn> Columns { get; } =
    [
        new("phoneNumber", "Recipient"),
        new("contactName", "Contact"),
        new("campaignName", "Campaign"),
        new("reason", "Reason"),
        new("errorCode", "Error code"),
        new("occurredAt", "Occurred at"),
    ];

    /// <inheritdoc />
    public async Task<int?> CountAsync(ExportQuery query, CancellationToken cancellationToken = default) =>
        await _queries.CountAsync(Filtered(query), cancellationToken);

    /// <inheritdoc />
    public async IAsyncEnumerable<object?[]> ReadAsync(
        ExportQuery query,
        IReadOnlyList<ExportColumn> columns,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(columns);

        var request = new PageRequest
        {
            SortBy = query.SortBy,
            SortDirection = query.Descending
                ? AppConstants.SortDirection.Descending
                : AppConstants.SortDirection.Ascending,
        };

        var projected = Filtered(query)
            .ApplySort(request, SortableColumns, failure => failure.OccurredOn)
            .ThenByDescending(failure => failure.Id)
            .Select(failure => new Row(
                failure.CampaignName,
                failure.ContactName,
                failure.PhoneNumber,
                failure.Reason,
                failure.ErrorCode,
                failure.OccurredOn));

        await foreach (var row in _queries.StreamAsync(projected, cancellationToken))
        {
            var cells = new object?[columns.Count];

            for (var index = 0; index < columns.Count; index++)
            {
                cells[index] = Cell(row, columns[index].Key);
            }

            yield return cells;
        }
    }

    /// <summary>Applies what the failure log's own screen filters by.</summary>
    /// <param name="query">Captured list-view state.</param>
    private IQueryable<DeliveryFailure> Filtered(ExportQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var source = _failures.Query();

        if (query.Search is { Length: > 0 } term)
        {
            source = source.WhereFailureMatches(term);
        }

        // An unparseable code matches nothing rather than being ignored. Silently dropping a
        // filter would hand somebody the whole log when they asked for one error.
        if (query.Filter("errorCode") is { Length: > 0 } code)
        {
            source = int.TryParse(code, out var parsed)
                ? source.Where(failure => failure.ErrorCode == parsed)
                : source.Where(_ => false);
        }

        if (query.Filter("campaignName") is { Length: > 0 } campaign)
        {
            source = source.Where(failure => failure.CampaignName == campaign);
        }

        return source;
    }

    private static object? Cell(Row row, string key) => key switch
    {
        "phoneNumber" => row.PhoneNumber,
        "contactName" => row.ContactName,
        "campaignName" => row.CampaignName,
        "reason" => row.Reason,
        "errorCode" => row.ErrorCode,
        "occurredAt" => row.OccurredOn,
        _ => null,
    };

    private sealed record Row(
        string CampaignName,
        string ContactName,
        string PhoneNumber,
        string Reason,
        int ErrorCode,
        DateTimeOffset OccurredOn);
}
