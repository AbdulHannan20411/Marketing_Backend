namespace Marketing.Application.DTOs.Email;

/// <summary>A variable a template offers, with a sample used by the preview and by test sends.</summary>
/// <param name="Name">Variable name.</param>
/// <param name="Description">What the value is.</param>
/// <param name="Sample">Sample value. Never real customer data.</param>
public sealed record EmailTemplateVariableResponse(string Name, string Description, string Sample);

/// <summary>A template as listed, without its bodies.</summary>
/// <param name="Key">Stable identifier, for example <c>auth.invitation</c>.</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">What the email is for.</param>
/// <param name="Category">Editor grouping: <c>layout</c>, <c>account</c>, <c>billing</c> or <c>payments</c>.</param>
/// <param name="Subject">Subject template.</param>
/// <param name="IsCustomised">Whether it differs from the shipped default - what a reset undoes.</param>
/// <param name="UpdatedAt">When a person last saved or reset it, or null if nobody has.</param>
/// <param name="UpdatedBy">Display name of that person, or null.</param>
public sealed record EmailTemplateSummaryResponse(
    string Key,
    string Name,
    string Description,
    string Category,
    string Subject,
    bool IsCustomised,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy);

/// <summary>A template in full, as opened in the editor.</summary>
/// <param name="Key">Stable identifier.</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">What the email is for.</param>
/// <param name="Category">Editor grouping.</param>
/// <param name="Subject">Subject template.</param>
/// <param name="IsCustomised">Whether it differs from the shipped default.</param>
/// <param name="UpdatedAt">When a person last saved or reset it.</param>
/// <param name="UpdatedBy">Display name of that person.</param>
/// <param name="HtmlBody">HTML body template.</param>
/// <param name="TextBody">Plain-text body template.</param>
/// <param name="Variables">The template's own variables. System variables are not repeated here.</param>
public sealed record EmailTemplateResponse(
    string Key,
    string Name,
    string Description,
    string Category,
    string Subject,
    bool IsCustomised,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy,
    string HtmlBody,
    string TextBody,
    IReadOnlyList<EmailTemplateVariableResponse> Variables);

/// <summary>The three editable parts of a template, saved or sent as a test.</summary>
/// <param name="Subject">Subject template.</param>
/// <param name="HtmlBody">HTML body template.</param>
/// <param name="TextBody">Plain-text body template.</param>
public sealed record EmailTemplateDraftRequest(string? Subject, string? HtmlBody, string? TextBody);

/// <summary>Where a test email went.</summary>
/// <param name="SentTo">Always the signed-in staff member's own address.</param>
public sealed record TestEmailResponse(string SentTo);
