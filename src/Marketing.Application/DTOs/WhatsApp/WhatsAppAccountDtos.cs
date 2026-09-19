using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.DTOs.WhatsApp;

/// <summary>One of a workspace's WhatsApp numbers.</summary>
/// <param name="Id">Public id, <c>wa_…</c>.</param>
/// <param name="Label">What the workspace calls it - "Sales". Unique per workspace, 1-40 characters.</param>
/// <param name="DisplayPhoneNumber">Number in international display format.</param>
/// <param name="VerifiedName">Business name Meta has verified.</param>
/// <param name="WabaId">Business account the number belongs to; templates belong to it too.</param>
/// <param name="PhoneNumberId">Meta's id for the number.</param>
/// <param name="Status">Connection state, with an already-lapsed token reported as <c>error</c>.</param>
/// <param name="QualityRating">Meta's quality rating.</param>
/// <param name="MessagingTier">Meta's daily unique-customer ceiling.</param>
/// <param name="MessagingLimit">Rolling 24-hour messaging ceiling.</param>
/// <param name="MessagesLast24h">Messages sent in the rolling 24-hour window.</param>
/// <param name="TokenExpiresAt">Null means no stated expiry, not expired.</param>
/// <param name="ConnectedAt">When it was last connected.</param>
/// <param name="IsDefault">Whether it is the workspace default. Exactly one is, while any exist.</param>
/// <param name="MyPermissions">What the caller may do on it. Administrators get all three.</param>
/// <param name="Health">The signals the client derives its traffic light from.</param>
public sealed record WhatsAppAccountResponse(
    string Id,
    string Label,
    string DisplayPhoneNumber,
    string VerifiedName,
    string WabaId,
    string PhoneNumberId,
    ConnectionStatus Status,
    QualityRating QualityRating,
    MessagingTier MessagingTier,
    int MessagingLimit,
    int MessagesLast24h,
    DateTimeOffset? TokenExpiresAt,
    DateTimeOffset? ConnectedAt,
    bool IsDefault,
    IReadOnlyList<WhatsAppAccessLevel> MyPermissions,
    WhatsAppAccountHealthResponse Health);

/// <summary>What is known about whether a number is working.</summary>
/// <param name="ApiStatus">Outcome of the latest Graph call for it: <c>ok</c>, <c>degraded</c>, <c>down</c>, <c>unknown</c>.</param>
/// <param name="PhoneNumberStatus">Meta's status for the number, passed through.</param>
/// <param name="AccountStatus">Meta's review status for the business account, passed through.</param>
/// <param name="LastWebhookAt">When Meta last delivered a webhook for it.</param>
/// <param name="LastMessageSentAt">When it last sent a message Meta accepted.</param>
/// <param name="LastMessageReceivedAt">When a customer last wrote to it.</param>
/// <param name="LastError">What last went wrong, in plain words.</param>
public sealed record WhatsAppAccountHealthResponse(
    string ApiStatus,
    string? PhoneNumberStatus,
    string? AccountStatus,
    DateTimeOffset? LastWebhookAt,
    DateTimeOffset? LastMessageSentAt,
    DateTimeOffset? LastMessageReceivedAt,
    string? LastError);

/// <summary>The numbers the caller may view, and the plan's allowance.</summary>
/// <param name="Items">Visible numbers, the default first.</param>
/// <param name="Limit">The plan's ceiling; null means unlimited.</param>
/// <param name="Used">Numbers in the whole workspace counting against it, whatever their status.</param>
/// <param name="MyDefaultAccountId">The caller's own default, when they may view one.</param>
public sealed record WhatsAppAccountListResponse(
    IReadOnlyList<WhatsAppAccountResponse> Items,
    int? Limit,
    int Used,
    string? MyDefaultAccountId);

/// <summary>Renames a number.</summary>
/// <param name="Label">The new name, 1-40 characters.</param>
public sealed record UpdateWhatsAppAccountRequest(string? Label);

/// <summary>One employee's access to one number.</summary>
/// <param name="AccountId">Public account id.</param>
/// <param name="Permissions">Non-empty, and containing <c>view</c> whenever it contains anything else.</param>
public sealed record WhatsAppAccessEntry(string AccountId, IReadOnlyList<WhatsAppAccessLevel> Permissions);

/// <summary>Replaces an employee's access to every number.</summary>
/// <param name="Access">The complete set; an empty list removes access to every number.</param>
/// <param name="DefaultAccountId">Null, or one of the ids in <paramref name="Access"/>.</param>
public sealed record UpdateWhatsAppAccessRequest(
    IReadOnlyList<WhatsAppAccessEntry>? Access,
    string? DefaultAccountId);
