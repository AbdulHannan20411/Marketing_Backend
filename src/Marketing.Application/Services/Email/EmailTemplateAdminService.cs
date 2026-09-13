using Marketing.Application.Configurations;
using Marketing.Application.DTOs.Email;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.DataAccess.Entities;
using Marketing.DataAccess.Seed;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Options;

namespace Marketing.Application.Services.Email;

/// <summary>Platform staff editing the transactional email templates.</summary>
public interface IEmailTemplateAdminService
{
    /// <summary>Every template without its bodies, in the shipped order.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<EmailTemplateSummaryResponse>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>One template in full.</summary>
    /// <param name="key">Template key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="EmailTemplateNotFoundException">No such template.</exception>
    public Task<EmailTemplateResponse> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Saves new wording for a template.</summary>
    /// <param name="key">Template key.</param>
    /// <param name="request">The subject and both bodies.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="EmailTemplateNotFoundException">No such template.</exception>
    /// <exception cref="ValidationException">The draft would not render.</exception>
    public Task<EmailTemplateResponse> UpdateAsync(
        string key,
        EmailTemplateDraftRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Sends an unsaved draft, rendered with sample values, to the signed-in staff member.</summary>
    /// <param name="key">Template key.</param>
    /// <param name="request">The draft to render.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="EmailTemplateNotFoundException">No such template.</exception>
    /// <exception cref="ValidationException">The draft would not render.</exception>
    public Task<TestEmailResponse> SendTestAsync(
        string key,
        EmailTemplateDraftRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a template to the wording it shipped with.</summary>
    /// <param name="key">Template key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="EmailTemplateNotFoundException">No such template.</exception>
    /// <exception cref="BusinessRuleException">The template has no shipped default.</exception>
    public Task<EmailTemplateResponse> ResetAsync(string key, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IEmailTemplateAdminService" />
public sealed class EmailTemplateAdminService : IEmailTemplateAdminService
{
    /// <summary>
    /// Stands in for an email while the layout itself is being tested.
    /// </summary>
    /// <remarks>
    /// Identical to the editor's own layout preview body, so a test send looks like the preview the
    /// person was just looking at.
    /// </remarks>
    private static readonly EmailTemplateDraft LayoutPreviewBody = new(
        "Layout preview",
        """<h1 style="margin:0 0 12px 0;font-size:22px;line-height:30px;font-weight:700;color:#1e293b;">Every email appears here</h1><p style="margin:0;font-size:15px;line-height:24px;color:#475569;">This is placeholder content. Each template's body is placed where the layout has its content slot.</p>""",
        "Every email appears here.\n\nThis is placeholder content.");

    /// <summary>The layout's preheader sample, matching the editor's.</summary>
    private const string LayoutPreheaderSample = "Your workspace is ready.";

    private readonly IRepository<EmailTemplate> _templates;
    private readonly IRepository<User> _users;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly EmailTemplateCache _cache;
    private readonly IEmailTemplateRenderer _renderer;
    private readonly IEmailSender _email;
    private readonly ICurrentUser _currentUser;
    private readonly IDateTimeProvider _clock;
    private readonly EmailOptions _options;

    /// <summary>Initialises a new instance.</summary>
    public EmailTemplateAdminService(
        IRepository<EmailTemplate> templates,
        IRepository<User> users,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        EmailTemplateCache cache,
        IEmailTemplateRenderer renderer,
        IEmailSender email,
        ICurrentUser currentUser,
        IDateTimeProvider clock,
        IOptions<EmailOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _templates = templates;
        _users = users;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _cache = cache;
        _renderer = renderer;
        _email = email;
        _currentUser = currentUser;
        _clock = clock;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EmailTemplateSummaryResponse>> ListAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _queries.ToListAsync(_templates.Query(), cancellationToken);
        var names = await LoadEditorNamesAsync(rows.Select(row => row.UpdatedByUserId), cancellationToken);

        return
        [
            .. rows
                .OrderBy(row => EmailTemplateDefaults.OrderOf(row.Key))
                .ThenBy(row => row.Key, StringComparer.Ordinal)
                .Select(row => ToSummary(row, names)),
        ];
    }

    /// <inheritdoc />
    public async Task<EmailTemplateResponse> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var row = await LoadAsync(key, tracked: false, cancellationToken);

        return await ToResponseAsync(row, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<EmailTemplateResponse> UpdateAsync(
        string key,
        EmailTemplateDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        var row = await LoadAsync(key, tracked: true, cancellationToken);
        var draft = ToDraft(request);

        EnsureValid(row, draft);

        row.Subject = draft.Subject;
        row.HtmlBody = draft.HtmlBody;
        row.TextBody = draft.TextBody;
        row.UpdatedOn = _clock.UtcNow;
        row.UpdatedByUserId = _currentUser.UserId;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Evicted after the save, not before: evicting first would let a send in between re-cache the
        // old wording for the full cache lifetime.
        _cache.Evict(row.Key);

        return await ToResponseAsync(row, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TestEmailResponse> SendTestAsync(
        string key,
        EmailTemplateDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        var row = await LoadAsync(key, tracked: false, cancellationToken);
        var draft = ToDraft(request);

        EnsureValid(row, draft);

        // Only ever the signed-in staff member. There is no recipient field on purpose, so this cannot
        // be used to send arbitrary mail under the platform's name.
        var recipient = _currentUser.Email;

        if (string.IsNullOrWhiteSpace(recipient))
        {
            throw new ValidationException("recipient", "Your account has no email address to send a test to.");
        }

        var isLayout = string.Equals(row.Key, EmailTemplateLanguage.LayoutKey, StringComparison.Ordinal);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var variable in EmailTemplateDefaults.DeserialiseVariables(row.VariablesJson))
        {
            values[variable.Name] = variable.Sample;
        }

        // Real configuration rather than samples, so the test shows the product name, support address
        // and links the customer will actually see.
        foreach (var (name, value) in EmailSystemValues.Build(_options, _clock.UtcNow))
        {
            values[name] = value;
        }

        RenderedEmail rendered;

        if (isLayout)
        {
            // Testing the layout: a placeholder body inside the draft layout.
            values["preheader"] = LayoutPreheaderSample;
            rendered = EmailTemplateLanguage.RenderEmail(LayoutPreviewBody, draft, values);
        }
        else
        {
            // Testing a body: the draft inside the layout as currently saved.
            var layout = await _renderer.ResolveAsync(EmailTemplateLanguage.LayoutKey, cancellationToken);
            rendered = EmailTemplateLanguage.RenderEmail(draft, layout, values);
        }

        await _email.SendAsync(
            new EmailMessage(
                recipient,
                string.IsNullOrWhiteSpace(_currentUser.DisplayName) ? recipient : _currentUser.DisplayName,
                "[Test] " + WithoutControlCharacters(rendered.Subject),
                rendered.Html,
                rendered.Text),
            cancellationToken);

        return new TestEmailResponse(recipient);
    }

    /// <inheritdoc />
    public async Task<EmailTemplateResponse> ResetAsync(string key, CancellationToken cancellationToken = default)
    {
        var row = await LoadAsync(key, tracked: true, cancellationToken);

        var shipped = EmailTemplateDefaults.Find(row.Key)
                      ?? throw new BusinessRuleException(
                          "email_template_no_default",
                          "This template has no shipped default to return to.");

        row.Subject = shipped.Subject;
        row.HtmlBody = shipped.HtmlBody;
        row.TextBody = shipped.TextBody;
        row.DefaultHash = EmailTemplateDefaults.Hash(shipped.Subject, shipped.HtmlBody, shipped.TextBody);
        row.UpdatedOn = _clock.UtcNow;
        row.UpdatedByUserId = _currentUser.UserId;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        _cache.Evict(row.Key);

        return await ToResponseAsync(row, cancellationToken);
    }

    private async Task<EmailTemplate> LoadAsync(string key, bool tracked, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new EmailTemplateNotFoundException(key ?? string.Empty);
        }

        return await _queries.FirstOrDefaultAsync(
                   _templates.Query(asNoTracking: !tracked).Where(template => template.Key == key),
                   cancellationToken)
               ?? throw new EmailTemplateNotFoundException(key);
    }

    private static void EnsureValid(EmailTemplate row, EmailTemplateDraft draft)
    {
        var rules = EmailTemplateLanguage.RulesFor(
            row.Key,
            EmailTemplateDefaults.DeserialiseVariables(row.VariablesJson).Select(variable => variable.Name));

        var problems = EmailTemplateLanguage.Validate(draft, rules);

        if (problems.Count > 0)
        {
            // Every problem at once, under one key. The editor blocks all of these already, so this
            // only answers someone calling the API directly - and they deserve the whole list.
            throw new ValidationException(
                new Dictionary<string, string[]>(StringComparer.Ordinal) { ["Template"] = [.. problems] });
        }
    }

    private static EmailTemplateDraft ToDraft(EmailTemplateDraftRequest? request) =>
        new(request?.Subject ?? string.Empty, request?.HtmlBody ?? string.Empty, request?.TextBody ?? string.Empty);

    /// <summary>Whether a template differs from the wording it shipped with.</summary>
    /// <remarks>
    /// Compared against the shipped default itself, as the editor's mock does. A template with no
    /// shipped default has nothing to be reset to, so it counts as customised.
    /// </remarks>
    private static bool IsCustomised(EmailTemplate row)
    {
        var shipped = EmailTemplateDefaults.Find(row.Key);

        return shipped is null
               || !string.Equals(row.Subject, shipped.Subject, StringComparison.Ordinal)
               || !string.Equals(row.HtmlBody, shipped.HtmlBody, StringComparison.Ordinal)
               || !string.Equals(row.TextBody, shipped.TextBody, StringComparison.Ordinal);
    }

    private static EmailTemplateSummaryResponse ToSummary(EmailTemplate row, IReadOnlyDictionary<long, string> names) =>
        new(
            row.Key,
            row.Name,
            row.Description,
            row.Category,
            row.Subject,
            IsCustomised(row),
            row.UpdatedOn,
            row.UpdatedByUserId is { } editor && names.TryGetValue(editor, out var name) ? name : null);

    private async Task<EmailTemplateResponse> ToResponseAsync(EmailTemplate row, CancellationToken cancellationToken)
    {
        var names = await LoadEditorNamesAsync([row.UpdatedByUserId], cancellationToken);
        var summary = ToSummary(row, names);

        return new EmailTemplateResponse(
            summary.Key,
            summary.Name,
            summary.Description,
            summary.Category,
            summary.Subject,
            summary.IsCustomised,
            summary.UpdatedAt,
            summary.UpdatedBy,
            row.HtmlBody,
            row.TextBody,
            [
                .. EmailTemplateDefaults.DeserialiseVariables(row.VariablesJson)
                    .Select(variable => new EmailTemplateVariableResponse(variable.Name, variable.Description, variable.Sample)),
            ]);
    }

    /// <summary>Display names of the people who last edited templates. Never ids or addresses.</summary>
    private async Task<IReadOnlyDictionary<long, string>> LoadEditorNamesAsync(
        IEnumerable<long?> editorIds,
        CancellationToken cancellationToken)
    {
        var wanted = editorIds.Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();

        if (wanted.Count == 0)
        {
            return new Dictionary<long, string>();
        }

        var editors = await _queries.ToListAsync(
            _users.Query()
                .Where(user => wanted.Contains(user.Id))
                .Select(user => new { user.Id, user.DisplayName }),
            cancellationToken);

        return editors.ToDictionary(editor => editor.Id, editor => editor.DisplayName);
    }

    private static string WithoutControlCharacters(string value) =>
        new([.. value.Where(character => !char.IsControl(character))]);
}
