using System.Text.Json.Serialization;
using Marketing.Common.Requests;
using static Marketing.Common.Constants.AppConstants;

namespace Marketing.Application.DTOs.Audit;

/// <summary>One change to one record, as the history panel shows it.</summary>
/// <param name="Id">Opaque identifier, prefixed <c>aud_</c>.</param>
/// <param name="EntityName">The public name the caller asked with, echoed back.</param>
/// <param name="EntityId">The record, as its public id.</param>
/// <param name="Action">Created, updated or deleted.</param>
/// <param name="UserId">Who made the change, or null when a background job did.</param>
/// <param name="UserName">Their name, or null for a background job. The client shows "Automatic".</param>
/// <param name="OccurredAt">Instant of the change, in UTC.</param>
/// <param name="Changes">
/// Only what changed: <c>{ "Name": { "old": "A", "new": "B" } }</c>, with no <c>old</c> on a create
/// and <c>{ "redacted": true }</c> where the value must not be kept. Passed through exactly as the
/// interceptor stored it - unchanged fields are never filled in.
/// </param>
public sealed record RecordHistoryEntry(
    string Id,
    string EntityName,
    string EntityId,
    AuditAction Action,
    string? UserId,
    string? UserName,
    DateTimeOffset OccurredAt,
    [property: JsonPropertyName("changes")] object Changes);

/// <summary>How a caller narrows a record's history.</summary>
/// <remarks>
/// Every filter applies before paging, so <c>totalItems</c> describes the filtered set rather than
/// the record's whole history.
/// </remarks>
public sealed class RecordHistoryQuery : PageRequest
{
    /// <summary>Rows the history panel renders per page.</summary>
    public const int PanelPageSize = 10;

    /// <summary>
    /// Largest page this endpoint serves.
    /// </summary>
    /// <remarks>
    /// Lower than the platform's usual ceiling: a change set is unbounded in width - a create
    /// carries every column of the record - so fifty entries can be a far larger response than
    /// fifty of anything else.
    /// </remarks>
    public const int PanelMaxPageSize = 50;

    /// <summary>Initialises a new instance with the history panel's page size.</summary>
    public RecordHistoryQuery() => SetDefaultPageSize(PanelPageSize);

    /// <summary>Kind of change to show, or null for all three.</summary>
    public AuditAction? Action { get; init; }

    /// <summary>Public id of the person whose changes to show, or null for everyone's.</summary>
    public string? UserId { get; init; }

    /// <summary>Earliest day to include, inclusive.</summary>
    public DateOnly? From { get; init; }

    /// <summary>Latest day to include, inclusive - the whole day, not the instant it begins.</summary>
    public DateOnly? To { get; init; }
}
