using static Marketing.Common.Constants.AppConstants;

namespace Marketing.DataAccess.Entities;

/// <summary>
/// One unit of asynchronous import work, waiting for a worker to claim it.
/// <para>
/// A database-backed outbox rather than a message broker. The row is written in the same
/// transaction as the batch it refers to, so there is no window in which an import exists with no
/// job queued, or a job is queued for a batch that was rolled back — the failure mode a separate
/// broker introduces and then has to be engineered back out of.
/// </para>
/// <para>
/// Swapping this for RabbitMQ later means replacing the claim query and the poller. Nothing above
/// <c>IImportJobQueue</c> knows which it is.
/// </para>
/// </summary>
public sealed class ImportJob : BaseEntity, IRequiresTenant
{
    /// <summary>What the worker should do.</summary>
    public ImportJobKind Kind { get; set; }

    /// <summary>Batch the work concerns.</summary>
    public long ContactImportBatchId { get; set; }

    /// <summary>
    /// Export the work concerns, for <see cref="ImportJobKind.ExportErrors"/> only.
    /// <para>
    /// Named for what it is rather than being a generic payload column: the export is the one kind
    /// of work that needs a second target, and a typed foreign key is checked by the database
    /// whereas a serialised blob is checked by nothing.
    /// </para>
    /// </summary>
    public long? ContactImportExportId { get; set; }

    /// <summary>User to notify when it finishes.</summary>
    public long RequestedByUserId { get; set; }

    /// <summary>Where the job has got to.</summary>
    public ImportJobState State { get; set; } = ImportJobState.Pending;

    /// <summary>
    /// Attempts made. Bounded, so a job that fails for a reason retrying cannot fix — a corrupt
    /// file, a deleted batch — stops rather than looping for ever.
    /// </summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// Not eligible before this instant. Set on a retry to back off, so a transient failure is not
    /// hammered once a second until the attempt limit is spent.
    /// </summary>
    public DateTimeOffset AvailableAt { get; set; }

    /// <summary>
    /// Instant a worker claimed it. Also the lease: a job claimed long ago belongs to a worker
    /// that died, and is reclaimed rather than left stuck.
    /// </summary>
    public DateTimeOffset? ClaimedAt { get; set; }

    /// <summary>Instant it finished, either way.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Why the last attempt failed.</summary>
    public string? LastError { get; set; }

    /// <summary>Batch navigation.</summary>
    public ContactImportBatch ContactImportBatch { get; set; } = null!;

    /// <summary>Export navigation, set only for export work.</summary>
    public ContactImportExport? ContactImportExport { get; set; }
}
