using Marketing.Common.Requests;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.DTOs.WhatsApp;

/// <summary>
/// The tenant's Meta connection.
/// <para>
/// When nothing is connected this is returned with <see cref="ConnectionStatus.Disconnected"/>
/// rather than a 404, because the client renders a connect prompt from it. A 404 would be an error
/// state; "not connected yet" is a normal one.
/// </para>
/// </summary>
/// <param name="Status">Connection state.</param>
/// <param name="DisplayPhoneNumber">Number in international display format.</param>
/// <param name="VerifiedName">Business name Meta has verified.</param>
/// <param name="BusinessProfileAbout">Profile "about" text.</param>
/// <param name="BusinessCategory">Business category.</param>
/// <param name="QualityRating">Meta's current quality rating.</param>
/// <param name="MessagingLimit">Rolling 24-hour ceiling.</param>
/// <param name="MessagesLast24h">Messages sent in the rolling window.</param>
/// <param name="MessagingTier">
/// Meta's daily ceiling on unique customers. Reported, never requested — the client renders it as a
/// fact rather than a setting.
/// </param>
/// <param name="ConnectedAt">Instant the connection was established.</param>
/// <param name="WebhookHealthy">Whether Meta's webhook is delivering.</param>
/// <param name="TemplateNamespaceAlias">Template namespace alias.</param>
public sealed record WhatsAppConnectionResponse(
    ConnectionStatus Status,
    string DisplayPhoneNumber,
    string VerifiedName,
    string BusinessProfileAbout,
    string BusinessCategory,
    QualityRating QualityRating,
    int MessagingLimit,
    int MessagesLast24h,
    MessagingTier MessagingTier,
    DateTimeOffset? ConnectedAt,
    bool WebhookHealthy,
    string TemplateNamespaceAlias)
{
    /// <summary>The shape returned when a tenant has never connected a number.</summary>
    public static WhatsAppConnectionResponse Disconnected() => new(
        ConnectionStatus.Disconnected,
        DisplayPhoneNumber: string.Empty,
        VerifiedName: string.Empty,
        BusinessProfileAbout: string.Empty,
        BusinessCategory: string.Empty,
        QualityRating.Green,
        MessagingLimit: 0,
        MessagesLast24h: 0,
        MessagingTier.Tier250,
        ConnectedAt: null,
        WebhookHealthy: false,
        TemplateNamespaceAlias: string.Empty);
}

/// <summary>A message template.</summary>
/// <param name="Id">Opaque identifier, prefixed <c>tpl_</c>.</param>
/// <param name="Name">Template name.</param>
/// <param name="Category">Meta category.</param>
/// <param name="Status">Meta review status.</param>
/// <param name="Language">BCP 47 language tag.</param>
/// <param name="HeaderText">Optional header.</param>
/// <param name="BodyText">Body, with <c>{{1}}</c> placeholders preserved for the client to highlight.</param>
/// <param name="FooterText">Optional footer.</param>
/// <param name="Variables">Ordered variable names; the first fills <c>{{1}}</c>.</param>
/// <param name="Buttons">Button labels.</param>
/// <param name="QualityScore">Meta's quality score.</param>
/// <param name="TimesUsed">How many campaigns have used it.</param>
/// <param name="UpdatedAt">Instant it last changed.</param>
/// <param name="RejectionReason">Why Meta rejected it, when it did.</param>
public sealed record MessageTemplateResponse(
    string Id,
    string Name,
    TemplateCategory Category,
    TemplateStatus Status,
    string Language,
    string? HeaderText,
    string BodyText,
    string? FooterText,
    IReadOnlyList<string> Variables,
    IReadOnlyList<string> Buttons,
    QualityRating QualityScore,
    int TimesUsed,
    DateTimeOffset UpdatedAt,
    string? RejectionReason);

/// <summary>Search, filter and paging for the templates screen.</summary>
/// <remarks>
/// Filters carry the literal <c>all</c> when cleared rather than being omitted, matching the
/// convention <c>/contacts</c> set. The server therefore never has to tell "parameter absent" apart
/// from "filter cleared", which are the same intent and would otherwise be two code paths.
/// </remarks>
public class TemplateQuery : PageRequest
{
    /// <summary>Sentinel meaning "do not filter".</summary>
    public const string All = "all";

    /// <summary>Cards the templates screen renders per page.</summary>
    public const int GridPageSize = 10;

    /// <summary>Initialises a new instance with the templates screen's page size.</summary>
    public TemplateQuery() => SetDefaultPageSize(GridPageSize);

    // Search is inherited from PageRequest. It is matched against the template's name and its
    // body, not only the name: an operator hunting for the template that mentions "shipped" will
    // not remember it is called order_shipped_v3, and a name-only search reads as broken.

    /// <summary>Approval state to filter by, or <c>all</c>.</summary>
    public string Status { get; init; } = All;

    /// <summary>Category to filter by, or <c>all</c>.</summary>
    public string Category { get; init; } = All;
}

/// <summary>Template counts by approval state, across the whole collection.</summary>
/// <remarks>
/// Deliberately unfiltered. The numbers render as badges on the status chips and have to stay still
/// while an operator clicks between them; counts that responded to the active filter would show
/// "Pending 3" and every other chip at zero, which answers nothing.
/// </remarks>
/// <param name="Total">Every template.</param>
/// <param name="Approved">Approved by Meta and usable.</param>
/// <param name="Pending">Submitted and awaiting a verdict.</param>
/// <param name="Rejected">Refused by Meta.</param>
/// <param name="Paused">Paused by Meta on recipient feedback.</param>
public sealed record TemplateStatusCountsResponse(
    int Total,
    int Approved,
    int Pending,
    int Rejected,
    int Paused);
