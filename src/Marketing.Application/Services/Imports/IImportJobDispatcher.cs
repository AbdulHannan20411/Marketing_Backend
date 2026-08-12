using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using static Marketing.Common.Constants.AppConstants;

namespace Marketing.Application.Services.Imports;

/// <summary>
/// Enqueues asynchronous import work.
/// <para>
/// The seam a message broker would sit behind. Callers say what needs doing; nothing above this
/// knows whether the work is picked up by a Quartz poller or a RabbitMQ consumer.
/// </para>
/// </summary>
public interface IImportJobDispatcher
{
    /// <summary>
    /// Queues work for a batch.
    /// </summary>
    /// <remarks>
    /// Does <b>not</b> save. The job row is written in the caller's transaction, so a batch and its
    /// job commit together or not at all — there is no window where one exists without the other.
    /// </remarks>
    /// <param name="batch">Batch the work concerns. May be unsaved.</param>
    /// <param name="kind">What the worker should do.</param>
    /// <param name="requestedByUserId">User to notify when it finishes.</param>
    public void Enqueue(ContactImportBatch batch, ImportJobKind kind, long requestedByUserId);

    /// <summary>
    /// Queues the generation of a failed-record workbook.
    /// </summary>
    /// <remarks>
    /// Its own method because export work carries a second target — the export row it fills in —
    /// and folding that into <see cref="Enqueue"/> would make a parameter meaningless for two of
    /// the three job kinds. Does not save, for the same reason as <see cref="Enqueue"/>.
    /// </remarks>
    /// <param name="export">Export to fill in. May be unsaved.</param>
    /// <param name="requestedByUserId">User to notify when it finishes.</param>
    public void EnqueueExport(ContactImportExport export, long requestedByUserId);
}

/// <inheritdoc cref="IImportJobDispatcher" />
public sealed class ImportJobDispatcher : IImportJobDispatcher
{
    private readonly IRepository<ImportJob> _jobs;
    private readonly IDateTimeProvider _clock;

    /// <summary>Initialises a new instance.</summary>
    public ImportJobDispatcher(IRepository<ImportJob> jobs, IDateTimeProvider clock)
    {
        _jobs = jobs;
        _clock = clock;
    }

    /// <inheritdoc />
    public void Enqueue(ContactImportBatch batch, ImportJobKind kind, long requestedByUserId)
    {
        ArgumentNullException.ThrowIfNull(batch);

        _jobs.Add(new ImportJob
        {
            TenantId = batch.TenantId,
            Kind = kind,

            // By navigation: on upload the batch is inserted in this same unit of work and has no
            // key yet, so assigning the foreign key would store a zero.
            ContactImportBatch = batch,
            RequestedByUserId = requestedByUserId,
            State = ImportJobState.Pending,
            AvailableAt = _clock.UtcNow,
        });
    }

    /// <inheritdoc />
    public void EnqueueExport(ContactImportExport export, long requestedByUserId)
    {
        ArgumentNullException.ThrowIfNull(export);

        _jobs.Add(new ImportJob
        {
            TenantId = export.TenantId,
            Kind = ImportJobKind.ExportErrors,
            ContactImportBatchId = export.ContactImportBatchId,
            ContactImportExport = export,
            RequestedByUserId = requestedByUserId,
            State = ImportJobState.Pending,
            AvailableAt = _clock.UtcNow,
        });
    }
}
