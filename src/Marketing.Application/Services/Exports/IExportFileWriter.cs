using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.Exports;

/// <summary>
/// Writes export rows into a file, one batch at a time.
/// </summary>
/// <remarks>
/// The seam between "what the data is" and "what the file looks like". A dataset knows neither the
/// format nor the destination; a writer knows neither the query nor the tenant.
/// <para>
/// Implementations are handed a destination stream and must write <em>through</em> it rather than
/// building the document in memory first. A million-row export is the case this design exists
/// for, and it is the case where holding the whole file is the difference between a worker that
/// runs and one that is killed.
/// </para>
/// </remarks>
public interface IExportFileWriter
{
    /// <summary>Which format this writer produces.</summary>
    public ExportFormat Format { get; }

    /// <summary>Extension including the dot, for example <c>.csv</c>.</summary>
    public string FileExtension { get; }

    /// <summary>Media type sent on download.</summary>
    public string ContentType { get; }

    /// <summary>
    /// Writes every row to the destination, reporting progress as it goes.
    /// </summary>
    /// <param name="destination">Stream to write to. The caller owns and disposes it.</param>
    /// <param name="columns">Headings, in order.</param>
    /// <param name="rows">Rows, in order. Each array matches <paramref name="columns"/>.</param>
    /// <param name="onProgress">
    /// Called with the running row count, at the writer's own batching interval rather than per
    /// row. Never called per row: the callback writes to the database and pushes over SignalR.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Rows written.</returns>
    public Task<int> WriteAsync(
        Stream destination,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<object?[]> rows,
        Func<int, CancellationToken, Task> onProgress,
        CancellationToken cancellationToken = default);
}
