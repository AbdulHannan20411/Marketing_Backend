using System.Text.Json;
using System.Text.Json.Serialization;

namespace Marketing.Application.Services.Exports;

/// <summary>
/// One column a dataset can export.
/// </summary>
/// <remarks>
/// The catalogue of these <b>is</b> the allow-list. A client names columns by
/// <paramref name="Key"/> and never by property path, so there is no request that can reach a
/// navigation, a hidden column or an expression.
/// </remarks>
/// <param name="Key">Stable wire name, for example <c>fullName</c>.</param>
/// <param name="Heading">Column heading written into the file.</param>
/// <param name="Default">Whether it is included when the caller names no columns at all.</param>
public sealed record ExportColumn(string Key, string Heading, bool Default = true);

/// <summary>
/// The list view's state at the moment Export was clicked.
/// </summary>
/// <remarks>
/// Serialised into the job row and read back by the worker, so the file is of what the user was
/// looking at rather than of whatever the view holds by the time a worker gets to it.
/// <para>
/// <see cref="Filters"/> is a loose bag on purpose - each list view has its own - but it is never
/// loose at the point of use: a dataset reads the handful of keys it understands and ignores the
/// rest, so a filter name nobody handles narrows nothing instead of reaching the database.
/// </para>
/// </remarks>
public sealed record ExportQuery
{
    /// <summary>Free-text search, matched the way the list view matches it.</summary>
    public string? Search { get; init; }

    /// <summary>The list view's filters, by name.</summary>
    public IReadOnlyDictionary<string, string> Filters { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sort key, from the list endpoint's own allow-list. Null for the default order.</summary>
    public string? SortBy { get; init; }

    /// <summary>Sort direction, as the list endpoint accepts it.</summary>
    public string? SortDirection { get; init; }

    /// <summary>Columns to write, in order. Empty means the dataset's defaults.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>Reads a filter, or null when the view did not set it.</summary>
    /// <param name="name">Filter name.</param>
    public string? Filter(string name) =>
        Filters.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>Whether the sort was requested descending.</summary>
    public bool Descending =>
        SortDirection is { Length: > 0 } direction
        && (direction.StartsWith("desc", StringComparison.OrdinalIgnoreCase));

    /// <summary>How the query is stored on the job row and read back by the worker.</summary>
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// A list view that can be exported.
/// </summary>
/// <remarks>
/// One implementation per exportable list. Everything generic - the job row, the queue, the
/// writers, storage, notification, expiry - is shared, and this is the only part that knows what
/// the data is. Adding a list view means adding one of these and registering it.
/// <para>
/// Implementations run <b>inside a worker</b>, not inside a request. They must read through the
/// application's ordinary repositories so the tenant filter applies, and must page rather than
/// materialise: a list view is allowed to hold a million rows.
/// </para>
/// </remarks>
public interface IExportDataset
{
    /// <summary>Registry key, as stored on the job and sent by the client. Lower case.</summary>
    public string Key { get; }

    /// <summary>What to call it in a file name, a toast and the history screen.</summary>
    public string DisplayName { get; }

    /// <summary>
    /// Permission a caller must hold to export this list.
    /// </summary>
    /// <remarks>
    /// The same permission the list itself requires. An export is a read of the whole list, so
    /// anything else would either block somebody who can already see the data or hand it to
    /// somebody who cannot.
    /// </remarks>
    public string Permission { get; }

    /// <summary>Every column this dataset can write, in their natural order.</summary>
    public IReadOnlyList<ExportColumn> Columns { get; }

    /// <summary>
    /// How many rows the export will contain, or null when counting is not worth it.
    /// </summary>
    /// <remarks>
    /// Null is a supported answer and drives indeterminate progress on the client, which is the
    /// honest option when a count would cost a second scan of a filtered million rows.
    /// </remarks>
    /// <param name="query">The captured list-view state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<int?> CountAsync(ExportQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams the rows, already narrowed to <paramref name="columns"/> and in their order.
    /// </summary>
    /// <remarks>
    /// Yields one row at a time from batched reads. The consumer writes each row and drops it, so
    /// peak memory is one batch rather than one export.
    /// </remarks>
    /// <param name="query">The captured list-view state.</param>
    /// <param name="columns">Resolved columns, already validated against <see cref="Columns"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public IAsyncEnumerable<object?[]> ReadAsync(
        ExportQuery query,
        IReadOnlyList<ExportColumn> columns,
        CancellationToken cancellationToken = default);
}

/// <summary>Finds the dataset behind a registry key.</summary>
public interface IExportDatasetRegistry
{
    /// <summary>Every registered dataset.</summary>
    public IReadOnlyList<IExportDataset> All { get; }

    /// <summary>The dataset with this key, or null when nothing is registered under it.</summary>
    /// <param name="key">Registry key. Matched without regard to case.</param>
    public IExportDataset? Find(string? key);
}

/// <inheritdoc cref="IExportDatasetRegistry" />
public sealed class ExportDatasetRegistry : IExportDatasetRegistry
{
    private readonly Dictionary<string, IExportDataset> _byKey;

    /// <summary>Initialises a new instance from everything the container has registered.</summary>
    /// <param name="datasets">Registered datasets.</param>
    public ExportDatasetRegistry(IEnumerable<IExportDataset> datasets)
    {
        ArgumentNullException.ThrowIfNull(datasets);

        All = [.. datasets];
        _byKey = All.ToDictionary(dataset => dataset.Key, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public IReadOnlyList<IExportDataset> All { get; }

    /// <inheritdoc />
    public IExportDataset? Find(string? key) =>
        key is { Length: > 0 } && _byKey.TryGetValue(key, out var dataset) ? dataset : null;
}
