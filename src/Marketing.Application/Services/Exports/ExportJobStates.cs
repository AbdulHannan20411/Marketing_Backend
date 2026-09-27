using Marketing.Common.Exceptions;
using Marketing.DataAccess.Entities;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.Exports;

/// <summary>
/// Which export states may follow which.
/// </summary>
/// <remarks>
/// Written down rather than left to whoever assigns the column next. Two things make an export's
/// state machine worth enforcing: a broker that may deliver the same message twice, and a cleanup
/// job that runs against rows a worker might be touching. Both are races, and both show up as a
/// transition that should never have been allowed - a completed export going back to processing,
/// an expired one being downloaded because something reopened it.
/// </remarks>
public static class ExportJobStates
{
    /// <summary>
    /// The allowed moves.
    /// </summary>
    /// <remarks>
    /// <c>Queued → Cancelled</c> and <c>Processing → Cancelled</c> are both here: a user may
    /// withdraw an export that has already started, and the worker notices at its next batch.
    /// <para>
    /// <c>Failed → Queued</c> is the retry. <c>Completed → Expired</c> is the cleanup job. Nothing
    /// leaves <c>Expired</c> or <c>Cancelled</c>; a new export is a new row, which keeps the
    /// history honest about what was asked for and when.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<ExportJobStatus, ExportJobStatus[]> Allowed = new()
    {
        [ExportJobStatus.Queued] = [ExportJobStatus.Processing, ExportJobStatus.Failed, ExportJobStatus.Cancelled],
        // Processing may go back to Queued, which looks like going backwards and is not: it is
        // the lease expiring. A worker that has been holding a job for an hour is a worker that
        // died, and the alternative to reclaiming it is an export stuck at 40% for ever.
        [ExportJobStatus.Processing] =
            [ExportJobStatus.Completed, ExportJobStatus.Failed, ExportJobStatus.Cancelled, ExportJobStatus.Queued],
        [ExportJobStatus.Completed] = [ExportJobStatus.Expired],
        [ExportJobStatus.Failed] = [ExportJobStatus.Queued],
        [ExportJobStatus.Cancelled] = [],
        [ExportJobStatus.Expired] = [],
    };

    /// <summary>States from which nothing further happens on its own.</summary>
    public static bool IsTerminal(ExportJobStatus status) =>
        status is ExportJobStatus.Completed
            or ExportJobStatus.Failed
            or ExportJobStatus.Cancelled
            or ExportJobStatus.Expired;

    /// <summary>Whether a move is allowed.</summary>
    /// <param name="from">Current state.</param>
    /// <param name="to">Proposed state.</param>
    public static bool CanMove(ExportJobStatus from, ExportJobStatus to) =>
        Allowed.TryGetValue(from, out var next) && next.Contains(to);

    /// <summary>
    /// Moves a job, refusing a transition the machine does not allow.
    /// </summary>
    /// <remarks>
    /// A <see cref="BusinessRuleException"/> rather than an assertion, because the caller that
    /// trips it is usually a redelivered message rather than a bug - and the consumer catches it,
    /// acknowledges and moves on instead of looping.
    /// </remarks>
    /// <param name="job">Job to move.</param>
    /// <param name="to">State to move it to.</param>
    /// <exception cref="BusinessRuleException">The move is not allowed.</exception>
    public static void MoveTo(ExportJob job, ExportJobStatus to)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (job.Status == to)
        {
            // Idempotent. Setting a state to what it already is is what a retry does, and it is
            // not a rule violation.
            return;
        }

        if (!CanMove(job.Status, to))
        {
            throw new BusinessRuleException(
                "export_transition_not_allowed",
                $"An export that is {job.Status} cannot become {to}.");
        }

        job.Status = to;
    }
}
