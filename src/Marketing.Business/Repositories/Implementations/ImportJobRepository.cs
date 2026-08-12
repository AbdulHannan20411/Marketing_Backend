using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Context;
using Marketing.DataAccess.Entities;
using Microsoft.EntityFrameworkCore;
using static Marketing.Common.Constants.AppConstants;

namespace Marketing.Business.Repositories.Implementations;

/// <inheritdoc cref="IImportJobRepository" />
public sealed class ImportJobRepository : Repository<ImportJob>, IImportJobRepository
{
    /// <summary>
    /// Claims and returns due jobs in one statement.
    /// <para>
    /// <c>FOR UPDATE SKIP LOCKED</c> is what makes this safe with several workers: each takes rows
    /// no other transaction holds, rather than blocking on them or reading the same row twice.
    /// Doing it as a read-then-update would let two workers claim the same job in the gap.
    /// </para>
    /// <para>
    /// <c>xmin</c> is named explicitly because <c>RETURNING *</c> expands to the table's own
    /// columns only. The concurrency token is mapped onto that system column, and without it every
    /// claim fails on materialisation rather than on anything to do with the query.
    /// </para>
    /// </summary>
    private const string ClaimSql = """
        UPDATE import_jobs
        SET    state = 'Claimed',
               claimed_at = {0},
               attempt_count = attempt_count + 1
        WHERE  id IN (
                   SELECT id
                   FROM   import_jobs
                   WHERE  is_deleted = false
                     AND  attempt_count < {3}
                     AND  (
                             (state = 'Pending' AND available_at <= {0})
                          OR (state = 'Claimed' AND claimed_at < {1})
                          )
                   ORDER BY available_at
                   LIMIT  {2}
                   FOR UPDATE SKIP LOCKED
               )
        RETURNING *, xmin;
        """;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="context">Database context.</param>
    public ImportJobRepository(ApplicationDbContext context)
        : base(context)
    {
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ImportJob>> ClaimDueAsync(
        DateTimeOffset utcNow,
        TimeSpan lease,
        int maximum,
        int maximumAttempts,
        CancellationToken cancellationToken = default)
    {
        // The poller has no principal, so the tenant filter would exclude every row. The projection
        // carries no tenant-owned data; the worker enters each job's tenant before touching
        // anything else.
        var claimed = await Set
            .FromSqlRaw(ClaimSql, utcNow, utcNow - lease, maximum, maximumAttempts)
            .IgnoreQueryFilters()
            .ToListAsync(cancellationToken);

        return claimed;
    }

    /// <summary>Marks a job finished, or schedules a retry with backoff.</summary>
    /// <param name="job">Job to settle.</param>
    /// <param name="succeeded">Whether the attempt worked.</param>
    /// <param name="error">Failure reason, when it did not.</param>
    /// <param name="utcNow">Current instant.</param>
    /// <param name="maximumAttempts">Attempts after which the job is dead-lettered.</param>
    public static void Settle(
        ImportJob job,
        bool succeeded,
        string? error,
        DateTimeOffset utcNow,
        int maximumAttempts)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (succeeded)
        {
            job.State = ImportJobState.Succeeded;
            job.CompletedAt = utcNow;
            job.LastError = null;

            return;
        }

        job.LastError = error;

        if (job.AttemptCount >= maximumAttempts)
        {
            // The dead letter. Kept rather than deleted, because "why did this import never
            // finish" is answered by this row and nothing else.
            job.State = ImportJobState.Failed;
            job.CompletedAt = utcNow;

            return;
        }

        // Exponential backoff. A transient failure - the database restarting, storage briefly
        // unavailable - should not spend the whole attempt budget inside a minute.
        job.State = ImportJobState.Pending;
        job.ClaimedAt = null;
        job.AvailableAt = utcNow.AddSeconds(Math.Pow(4, job.AttemptCount));
    }
}
