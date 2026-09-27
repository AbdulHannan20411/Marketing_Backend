using Marketing.Application.DTOs.Contacts;
using Marketing.Business.Extensions;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Helpers;
using Marketing.Common.Requests;
using Marketing.DataAccess.Entities;

namespace Marketing.Application.Services.Exports.Datasets;

/// <summary>
/// Exports the contacts list.
/// </summary>
/// <remarks>
/// The list view's filters, search and sort are applied by <see cref="ContactProjection"/> and
/// <c>ApplySort</c> - the same code the paged endpoint uses, called the same way. That is the
/// whole point: an export built from a second reading of "what does this filter mean" is an
/// export that eventually disagrees with the screen it came from, and the person who spots it
/// will be holding a spreadsheet they have already sent to somebody.
/// </remarks>
public sealed class ContactExportDataset : IExportDataset
{
    private readonly IRepository<Contact> _contacts;
    private readonly IQueryExecutor _queries;
    private readonly Audit.IActorNames _actors;

    /// <summary>Initialises a new instance.</summary>
    public ContactExportDataset(
        IRepository<Contact> contacts,
        IQueryExecutor queries,
        Audit.IActorNames actors)
    {
        _contacts = contacts;
        _queries = queries;
        _actors = actors;
    }

    /// <inheritdoc />
    public string Key => "contacts";

    /// <inheritdoc />
    public string DisplayName => "Contacts";

    /// <inheritdoc />
    public string Permission => Permissions.Contacts.View;

    /// <inheritdoc />
    public IReadOnlyList<ExportColumn> Columns { get; } =
    [
        new("id", "ID"),
        new("fullName", "Name"),
        new("phoneNumber", "Phone"),
        new("email", "Email"),
        new("country", "Country"),
        new("status", "Status"),
        new("optedInAt", "Opted in", Default: false),
        new("lastMessagedAt", "Last messaged"),
        new("createdAt", "Created"),
        new("createdBy", "Created by", Default: false),
        new("updatedAt", "Updated", Default: false),
        new("updatedBy", "Updated by", Default: false),
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
        ArgumentNullException.ThrowIfNull(columns);

        ArgumentNullException.ThrowIfNull(query);

        var request = new PageRequest
        {
            SortBy = query.SortBy,
            SortDirection = query.Descending
                ? AppConstants.SortDirection.Descending
                : AppConstants.SortDirection.Ascending,
        };

        var ordered = Filtered(query)
            .ApplySort(request, ContactProjection.SortableColumns, contact => contact.CreatedOn)

            // The same tiebreak the list uses. Without it a sort by status - four values over
            // hundreds of thousands of rows - has no defined order, and a streamed read has no
            // more right to a stable one than a paged read does.
            .ThenBy(contact => contact.Id);

        var wanted = columns.Select(column => column.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Only what was asked for. The audit names cost a join, so they are resolved lazily and
        // only when one of the two "by" columns is actually in the file.
        var needsNames = wanted.Contains("createdBy") || wanted.Contains("updatedBy");

        var projected = ordered.Select(contact => new Row(
            contact.Id,
            contact.FullName,
            contact.PhoneNumber,
            contact.Email,
            contact.Country,
            contact.Status,
            contact.OptedInAt,
            contact.LastMessagedAt,
            contact.CreatedOn,
            contact.CreatedBy,
            contact.ModifiedOn,
            contact.ModifiedBy));

        // Resolved once, before the reader opens. Doing it per row would be a query per row, and
        // doing it while the reader is open would want the connection the reader is holding.
        var names = needsNames
            ? await NamesAsync(ordered, cancellationToken)
            : new Dictionary<long, string>();

        // Streamed, not paged. One ordered cursor reads a million rows at constant memory, where
        // offset paging re-scans the index from the top for every batch and turns a large export
        // into quadratic work. The reader holds a connection for the run, which is affordable in
        // a worker and would not be in a request.
        await foreach (var row in _queries.StreamAsync(projected, cancellationToken))
        {
            var cells = new object?[columns.Count];

            for (var index = 0; index < columns.Count; index++)
            {
                cells[index] = Cell(row, columns[index].Key, names);
            }

            yield return cells;
        }
    }

    /// <summary>The list view's own filters, applied by the list view's own code.</summary>
    /// <param name="query">Captured list-view state.</param>
    private IQueryable<Contact> Filtered(ExportQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Rebuilt as the list endpoint's own query object, so every filter means exactly what it
        // means on screen - including the "all" sentinel and an unparseable id matching nothing.
        var listView = new ContactQuery
        {
            Search = query.Search,
            Status = query.Filter("status") ?? ContactQuery.All,
            GroupId = query.Filter("groupId") ?? ContactQuery.All,
            TagId = query.Filter("tagId") ?? ContactQuery.All,
        };

        return ContactProjection.ApplyFilters(_contacts.Query(), listView);
    }

    /// <summary>Display names for whoever created or last changed the rows in this export.</summary>
    /// <param name="source">The filtered, ordered contacts.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<IReadOnlyDictionary<long, string>> NamesAsync(
        IQueryable<Contact> source,
        CancellationToken cancellationToken)
    {
        // The distinct actors across the whole export, in one query. Bounded by the number of
        // people in the workspace rather than by the number of rows.
        var ids = await _queries.ToListAsync(
            source.Select(contact => contact.CreatedBy)
                .Concat(source.Where(contact => contact.ModifiedBy != null)
                    .Select(contact => contact.ModifiedBy!.Value))
                .Distinct(),
            cancellationToken);

        return await _actors.ResolveAsync([.. ids.Cast<long?>()], cancellationToken);
    }

    /// <summary>Reads one cell by its catalogue key.</summary>
    /// <param name="row">Materialised row.</param>
    /// <param name="key">Column key, already checked against the catalogue.</param>
    /// <param name="names">Resolved actor names.</param>
    private static object? Cell(Row row, string key, IReadOnlyDictionary<long, string> names) => key switch
    {
        "id" => PublicId.From(PublicId.Contact, row.Id),
        "fullName" => row.FullName,
        "phoneNumber" => row.PhoneNumber,
        "email" => row.Email,
        "country" => Countries.ToDisplayName(row.Country),
        "status" => row.Status.ToString(),
        "optedInAt" => row.OptedInAt,
        "lastMessagedAt" => row.LastMessagedAt,
        "createdAt" => row.CreatedOn,
        "createdBy" => Named(names, row.CreatedBy),
        "updatedAt" => row.ModifiedOn,
        "updatedBy" => Named(names, row.ModifiedBy),

        // Unreachable: the key came from this class's own catalogue. Null rather than a throw,
        // because failing an entire export over one unrecognised heading helps nobody.
        _ => null,
    };

    private static string? Named(IReadOnlyDictionary<long, string> names, long? userId) =>
        userId is { } id && names.TryGetValue(id, out var name) ? name : null;

    /// <summary>The columns read from the database, before they are narrowed to the chosen ones.</summary>
    private sealed record Row(
        long Id,
        string FullName,
        string PhoneNumber,
        string? Email,
        string Country,
        ContractEnums.ContactStatus Status,
        DateTimeOffset? OptedInAt,
        DateTimeOffset? LastMessagedAt,
        DateTimeOffset CreatedOn,
        long CreatedBy,
        DateTimeOffset? ModifiedOn,
        long? ModifiedBy);
}
