using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.DTOs.Workspace;

/// <summary>Request to switch a whole workspace off.</summary>
/// <remarks>
/// No workspace identifier, deliberately and permanently. The tenant comes from the token; an id in
/// the body would let any admin switch off any other workspace by guessing one.
/// </remarks>
/// <param name="Reason">Why. One of the fixed set shared with the client.</param>
/// <param name="Details">Free text. Required when the reason is <c>other</c>.</param>
/// <param name="CurrentPassword">Re-entered to prove the person is present, not just the session.</param>
public sealed record WorkspaceDeactivationRequest(
    DeactivationReason Reason,
    string? Details,
    string? CurrentPassword);

/// <summary>Outcome of switching a workspace off.</summary>
/// <param name="DeactivatedAt">When access ended.</param>
/// <param name="DataRetainedUntil">
/// Date the data stops being kept, or null when no promise is made. Shown to the customer verbatim,
/// which makes it a commitment rather than a note.
/// </param>
public sealed record WorkspaceDeactivationResponse(
    DateTimeOffset DeactivatedAt,
    DateOnly? DataRetainedUntil);
