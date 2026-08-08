using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.DTOs.Contacts;

/// <summary>Request to create a contact.</summary>
/// <param name="FullName">Full name.</param>
/// <param name="PhoneNumber">E.164 number.</param>
/// <param name="Email">Email address, when known.</param>
/// <param name="Country">ISO 3166-1 alpha-2 country code.</param>
/// <param name="Status">Marketing consent state.</param>
/// <param name="TagIds">Tags to apply.</param>
/// <param name="GroupIds">Groups to join.</param>
public sealed record CreateContactRequest(
    string FullName,
    string PhoneNumber,
    string? Email = null,
    string Country = "",
    ContactStatus Status = ContactStatus.Subscribed,
    IReadOnlyList<string>? TagIds = null,
    IReadOnlyList<string>? GroupIds = null);

/// <summary>
/// Request to update a contact.
/// <para>
/// Tag and group collections are replaced wholesale when supplied and left alone when omitted,
/// so the editor can save a name change without having to send the full membership back.
/// </para>
/// </summary>
public sealed record UpdateContactRequest(
    string? FullName = null,
    string? PhoneNumber = null,
    string? Email = null,
    string? Country = null,
    ContactStatus? Status = null,
    ContactLifecycle? Lifecycle = null,
    IReadOnlyList<string>? TagIds = null,
    IReadOnlyList<string>? GroupIds = null);

/// <summary>Request naming a set of contacts.</summary>
/// <param name="Ids">Contact identifiers.</param>
public sealed record BulkContactRequest(IReadOnlyList<string> Ids);

/// <summary>Request applying tags to a set of contacts.</summary>
/// <param name="Ids">Contact identifiers.</param>
/// <param name="TagIds">Tags to apply.</param>
/// <param name="Mode">
/// How the tags are applied. Defaults to <see cref="BulkMode.Add"/>, which is what the table's
/// "Add tag" action means - the operator is adding a label, not redefining every label a contact
/// carries.
/// </param>
public sealed record BulkTagRequest(
    IReadOnlyList<string> Ids,
    IReadOnlyList<string> TagIds,
    BulkMode Mode = BulkMode.Add);

/// <summary>Request adding a set of contacts to groups.</summary>
/// <param name="Ids">Contact identifiers.</param>
/// <param name="GroupIds">Groups to join.</param>
/// <param name="Mode">How the memberships are applied. Defaults to <see cref="BulkMode.Add"/>.</param>
public sealed record BulkGroupRequest(
    IReadOnlyList<string> Ids,
    IReadOnlyList<string> GroupIds,
    BulkMode Mode = BulkMode.Add);
