using System.Text.Json.Serialization;
using Marketing.Common.Requests;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.DTOs.Contacts;

/// <summary>How two contacts are judged to be the same person.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DuplicateStrategy>))]
public enum DuplicateStrategy
{
    /// <summary>
    /// Same normalised number. The only strictly reliable strategy, because uniqueness is enforced
    /// on exactly this value.
    /// </summary>
    Phone,

    /// <summary>Same email address. Households legitimately share one, so review before merging.</summary>
    Email,

    /// <summary>Same full name. The loosest strategy, offered for review only.</summary>
    Name,
}

/// <summary>Query parameters for the duplicate report.</summary>
public sealed class DuplicateQuery : PageRequest
{
    /// <summary>Matching strategy.</summary>
    public DuplicateStrategy Strategy { get; init; } = DuplicateStrategy.Phone;
}

/// <summary>A set of contacts that appear to be the same person.</summary>
/// <param name="MatchValue">The value that collided.</param>
/// <param name="Strategy">How they were matched.</param>
/// <param name="Contacts">The colliding contacts, oldest first.</param>
public sealed record DuplicateGroupResponse(
    string MatchValue,
    DuplicateStrategy Strategy,
    IReadOnlyList<ContactResponse> Contacts);

/// <summary>Request to fold several contacts into one.</summary>
/// <param name="KeepId">The contact that survives.</param>
/// <param name="MergeIds">Contacts folded into it and then deleted.</param>
/// <param name="FieldOverrides">
/// Values applied to the survivor after the merge, keyed by field name. Supported keys are
/// <c>fullName</c>, <c>email</c>, <c>country</c> and <c>phoneNumber</c>.
/// </param>
public sealed record MergeContactsRequest(
    string KeepId,
    IReadOnlyList<string> MergeIds,
    IReadOnlyDictionary<string, string?>? FieldOverrides = null);

/// <summary>How a bulk operation treats the values it is given.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BulkMode>))]
public enum BulkMode
{
    /// <summary>Adds what is missing and leaves the rest alone. Applying twice is a no-op.</summary>
    Add,

    /// <summary>Removes the named assignments, leaving others in place.</summary>
    Remove,

    /// <summary>Makes the named set the complete set, dropping anything else.</summary>
    Replace,
}

/// <summary>One item in a bulk request that could not be processed.</summary>
/// <param name="Id">Identifier as the caller supplied it.</param>
/// <param name="Reason">Why it was not processed.</param>
public sealed record BulkItemFailure(string Id, string Reason);

/// <summary>
/// Outcome of a bulk operation.
/// <para>
/// Reported per item rather than as a single success flag, because partial failure is the normal
/// case: a selection built across several pages routinely contains a row somebody else has since
/// deleted, and failing the whole call for it would be useless to the operator.
/// </para>
/// </summary>
/// <param name="Requested">Identifiers supplied.</param>
/// <param name="Succeeded">Identifiers acted on.</param>
/// <param name="Failed">Identifiers that could not be acted on, with reasons.</param>
public sealed record BulkOperationResult(
    int Requested,
    int Succeeded,
    IReadOnlyList<BulkItemFailure> Failed);

/// <summary>Request naming contacts to add to or remove from a group or tag.</summary>
/// <param name="ContactIds">Contact identifiers.</param>
public sealed record MembershipRequest(IReadOnlyList<string> ContactIds);

/// <summary>Query parameters for the contact export.</summary>
/// <remarks>
/// Inherits every list filter so "export what I am looking at" returns the same rows the table is
/// showing, and adds <see cref="Ids"/> for "export my selection".
/// </remarks>
public sealed class ContactExportQuery : ContactQuery
{
    /// <summary>
    /// Explicit contacts to export. When present, every other filter is ignored - a selection is
    /// already the answer to "which rows", and intersecting it with a filter would silently drop
    /// rows the operator ticked.
    /// </summary>
    public IReadOnlyList<string>? Ids { get; init; }
}
