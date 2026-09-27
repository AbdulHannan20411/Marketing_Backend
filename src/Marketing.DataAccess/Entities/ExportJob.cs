using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.DataAccess.Entities;

/// <summary>
/// One list-view export, from the moment a user asks for it to the moment its file expires.
/// </summary>
/// <remarks>
/// <b>The durable record.</b> The broker carries an identifier and nothing else; the cache holds a
/// progress counter that may vanish. Everything that has to survive a restart, a redelivery or a
/// second instance is this row — which state the job is in, who may download it, and where the
/// file went.
/// <para>
/// Deliberately not the same table as <see cref="ContactImportExport"/>. That one is the error
/// report for a particular import and is addressed through the import that produced it; this is a
/// dataset, a filter set and a format, and generalising the first into the second would have made
/// every import export carry six columns that mean nothing to it.
/// </para>
/// </remarks>
public sealed class ExportJob : BaseEntity, IRequiresTenant
{
    /// <summary>
    /// Which list view was exported, as a registry key such as <c>contacts</c>.
    /// </summary>
    /// <remarks>
    /// A key rather than an enum: a dataset is added by registering a handler, and an enum would
    /// mean a migration every time somebody makes one more list exportable. Validated against the
    /// registry on the way in, so the column only ever holds a name the application knows.
    /// </remarks>
    public required string Dataset { get; set; }

    /// <summary>What the file is written as.</summary>
    public ExportFormat Format { get; set; }

    /// <summary>Where the job has got to.</summary>
    public ExportJobStatus Status { get; set; } = ExportJobStatus.Queued;

    /// <summary>
    /// User who asked for it, and the only one who may download it.
    /// </summary>
    /// <remarks>
    /// Explicit rather than read from <c>CreatedBy</c>, following <see cref="ImportJob"/>: this is
    /// functional - who to notify, and whose file this is - and the audit column is bookkeeping
    /// that a future change to attribution could legitimately move.
    /// </remarks>
    public long RequestedByUserId { get; set; }

    /// <summary>
    /// The list view's state at the moment Export was clicked, as JSON.
    /// </summary>
    /// <remarks>
    /// Stored rather than re-derived, because the export must be of what the user was looking at:
    /// the filters could change, or a contact could be edited, between the click and the worker
    /// picking the job up. Read back through the dataset's own typed query object, so a column or
    /// filter name that is not on that dataset's allow-list is refused rather than reaching the
    /// database.
    /// </remarks>
    public required string QueryJson { get; set; }

    /// <summary>
    /// Columns the user chose, in their order, or empty for the dataset's default set.
    /// </summary>
    /// <remarks>
    /// Names from the handler's own catalogue, checked when the job is created. Never a property
    /// path or an expression - a client that could name a column could name a navigation.
    /// </remarks>
    public List<string> Columns { get; set; } = [];

    /// <summary>
    /// Rows the export will contain, or null when it was not worth counting up front.
    /// </summary>
    /// <remarks>
    /// Null is a supported answer, not a missing one: it drives indeterminate progress on the
    /// client. A count over a filtered million-row view is a second full scan, and for some
    /// datasets that costs more than the export.
    /// </remarks>
    public int? TotalRecords { get; set; }

    /// <summary>Rows written so far. Updated per batch, never per row.</summary>
    public int ProcessedRecords { get; set; }

    /// <summary>Instant a worker claimed it.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>Instant it reached a terminal state, either way.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Instant the file stops being downloadable. Set when the file is written.
    /// </summary>
    /// <remarks>
    /// Checked on download as well as by the cleanup job, so a file whose deletion has not run yet
    /// is still refused. The row outlives the file; the history stays readable.
    /// </remarks>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Name the browser saves the file as. Sanitised when it is built.</summary>
    public string? FileName { get; set; }

    /// <summary>
    /// Opaque key from <c>IFileStorage</c>. Never a path, and never returned to a client.
    /// </summary>
    public string? FileStorageKey { get; set; }

    /// <summary>Size of the written file, for the history screen and for logs.</summary>
    public long? FileSizeBytes { get; set; }

    /// <summary>
    /// Why it failed, in words a user can read.
    /// </summary>
    /// <remarks>
    /// Never an exception message. The technical detail goes to the log with the job id attached;
    /// a stack trace or a database error reaching the client is both useless to them and a
    /// disclosure.
    /// </remarks>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Attempts made, so a job the broker keeps redelivering stops rather than looping.
    /// </summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// Hash of the dataset, format and query, used to recognise the same export asked for twice.
    /// </summary>
    /// <remarks>
    /// Not a uniqueness constraint. Somebody may legitimately export the same view again an hour
    /// later; what this catches is the same request arriving five times because the button was
    /// clicked five times, which is a UI event rather than five intentions.
    /// </remarks>
    public required string Fingerprint { get; set; }
}
