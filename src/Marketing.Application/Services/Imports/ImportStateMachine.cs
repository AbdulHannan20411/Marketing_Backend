using Marketing.Common.Exceptions;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.Imports;

/// <summary>
/// The only place an import batch's status is allowed to change.
/// <para>
/// Stated as an explicit allow-list rather than a "is it different" check, because the invalid
/// moves are the dangerous ones. A batch that goes back to <c>AwaitingConfirmation</c> after
/// committing could be committed twice, and with a message broker or a retried job that is not a
/// hypothetical — it is what happens the first time a worker is redelivered.
/// </para>
/// </summary>
internal static class ImportStateMachine
{
    /// <summary>
    /// Every legal transition. A status absent from the keys is terminal.
    /// </summary>
    private static readonly Dictionary<ContactImportStatus, ContactImportStatus[]> Allowed = new()
    {
        [ContactImportStatus.Queued] =
            [ContactImportStatus.Processing, ContactImportStatus.Failed, ContactImportStatus.Cancelled],

        [ContactImportStatus.Processing] =
            [ContactImportStatus.AwaitingMapping, ContactImportStatus.Failed, ContactImportStatus.Cancelled],

        // Mapping may be supplied with the upload, in which case the worker skips straight to
        // awaiting confirmation without a round trip through the wizard.
        [ContactImportStatus.AwaitingMapping] =
            [ContactImportStatus.AwaitingConfirmation, ContactImportStatus.Cancelled],

        [ContactImportStatus.AwaitingConfirmation] =
            [
                ContactImportStatus.Committing,
                ContactImportStatus.AwaitingMapping,
                ContactImportStatus.Cancelled,
            ],

        [ContactImportStatus.Committing] =
            [
                ContactImportStatus.Completed,
                ContactImportStatus.CompletedWithErrors,
                ContactImportStatus.Failed,
            ],
    };

    /// <summary>Statuses from which no further transition is possible.</summary>
    public static bool IsTerminal(ContactImportStatus status) => !Allowed.ContainsKey(status);

    /// <summary>Whether a transition is legal.</summary>
    /// <param name="from">Current status.</param>
    /// <param name="to">Requested status.</param>
    public static bool CanTransition(ContactImportStatus from, ContactImportStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>
    /// Moves a batch to a new status, or refuses.
    /// </summary>
    /// <param name="batch">Batch to move.</param>
    /// <param name="to">Requested status.</param>
    /// <exception cref="BusinessRuleException">The transition is not legal.</exception>
    public static void Transition(DataAccess.Entities.ContactImportBatch batch, ContactImportStatus to)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (!CanTransition(batch.Status, to))
        {
            throw new BusinessRuleException(
                "invalid_import_transition",
                $"An import that is {Describe(batch.Status)} cannot become {Describe(to)}.");
        }

        batch.Status = to;
    }

    /// <summary>
    /// Whether a worker should pick this batch up, given the status it expects to find.
    /// </summary>
    /// <remarks>
    /// This is the idempotency guard. A redelivered or retried job finds the batch already past the
    /// status it expected and does nothing, rather than importing the same file twice.
    /// </remarks>
    /// <param name="batch">Batch the worker claimed.</param>
    /// <param name="expected">Status the work is only valid from.</param>
    public static bool ShouldProcess(DataAccess.Entities.ContactImportBatch batch, ContactImportStatus expected)
    {
        ArgumentNullException.ThrowIfNull(batch);

        return batch.Status == expected;
    }

    /// <summary>Human-readable status, for messages the operator reads.</summary>
    private static string Describe(ContactImportStatus status) => status switch
    {
        ContactImportStatus.Queued => "queued",
        ContactImportStatus.Processing => "being read",
        ContactImportStatus.AwaitingMapping => "waiting for column mapping",
        ContactImportStatus.AwaitingConfirmation => "waiting for confirmation",
        ContactImportStatus.Committing => "being imported",
        ContactImportStatus.Completed => "completed",
        ContactImportStatus.CompletedWithErrors => "completed with errors",
        ContactImportStatus.Failed => "failed",
        ContactImportStatus.Cancelled => "cancelled",
        _ => status.ToString().ToLowerInvariant(),
    };
}
