using Marketing.Application.DTOs.WhatsApp;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Marketing.Common.Responses;
using Microsoft.EntityFrameworkCore;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services;

/// <summary>Reads and refreshes the tenant's Meta connection and templates.</summary>
public interface IWhatsAppService
{
    /// <summary>Returns a number's connection, or a disconnected placeholder when there is none.</summary>
    /// <param name="accountId">Public account id, or null for the workspace default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<WhatsAppConnectionResponse> GetConnectionAsync(
        string? accountId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Refreshes a number from Meta and returns the updated state.</summary>
    /// <param name="accountId">Public account id, or null for the workspace default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<WhatsAppConnectionResponse> SyncConnectionAsync(
        string? accountId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns every template of a number's business account.</summary>
    /// <param name="accountId">Public account id, or null for the workspace default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<MessageTemplateResponse>> GetTemplatesAsync(
        string? accountId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a filtered, searched page of a number's templates.</summary>
    /// <param name="query">Search, filters and paging.</param>
    /// <param name="accountId">Public account id, or null for the workspace default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PagedResult<MessageTemplateResponse>> SearchTemplatesAsync(
        TemplateQuery query,
        string? accountId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Counts a number's templates by approval state, ignoring any active filter.</summary>
    /// <param name="accountId">Public account id, or null for the workspace default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<TemplateStatusCountsResponse> CountTemplatesAsync(
        string? accountId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns every approved template of a number, unpaged, for the campaign picker.</summary>
    /// <param name="accountId">Public account id, or null for the workspace default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<MessageTemplateResponse>> GetApprovedTemplatesAsync(
        string? accountId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Refreshes a number's templates from Meta and returns them.</summary>
    /// <param name="accountId">Public account id, or null for the workspace default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<MessageTemplateResponse>> SyncTemplatesAsync(
        string? accountId = null,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IWhatsAppService" />
public sealed class WhatsAppService : IWhatsAppService
{
    private readonly Services.WhatsApp.IWhatsAppAccessService _access;
    private readonly Services.WhatsApp.IWhatsAppAccountService _accounts;
    private readonly Services.WhatsApp.ITemplateHeaderSampleService _headerSamples;
    private readonly IRepository<MessageTemplate> _templates;
    private readonly IQueryExecutor _queries;
    private readonly IWhatsAppGateway _gateway;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISecretProtector _protector;
    private readonly ITenantContext _tenantContext;
    private readonly IDateTimeProvider _clock;

    /// <summary>Initialises a new instance.</summary>
    public WhatsAppService(
        Services.WhatsApp.IWhatsAppAccessService access,
        Services.WhatsApp.IWhatsAppAccountService accounts,
        Services.WhatsApp.ITemplateHeaderSampleService headerSamples,
        IRepository<MessageTemplate> templates,
        IQueryExecutor queries,
        IWhatsAppGateway gateway,
        IUnitOfWork unitOfWork,
        ISecretProtector protector,
        ITenantContext tenantContext,
        IDateTimeProvider clock)
    {
        _access = access;
        _accounts = accounts;
        _headerSamples = headerSamples;
        _templates = templates;
        _queries = queries;
        _gateway = gateway;
        _unitOfWork = unitOfWork;
        _protector = protector;
        _tenantContext = tenantContext;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<WhatsAppConnectionResponse> GetConnectionAsync(
        string? accountId = null,
        CancellationToken cancellationToken = default)
    {
        // Loaded as an entity rather than projected in SQL: the onboarding steps are a JSON
        // document on the row, and the derived "still running" flag the client polls on cannot be
        // built by the database.
        var connection = await _access.ResolveAsync(
            accountId,
            Common.Constants.ContractEnums.WhatsAppAccessLevel.View,
            cancellationToken);

        // Never a 404. "No number connected yet" is a normal state the client renders a connect
        // prompt from, whereas a 404 would put the screen into an error state.
        if (connection is null)
        {
            return WhatsAppConnectionResponse.Disconnected();
        }

        return connection.ToResponse().WithExpiryApplied(_clock.UtcNow);
    }

    /// <inheritdoc />
    public async Task<WhatsAppConnectionResponse> SyncConnectionAsync(
        string? accountId = null,
        CancellationToken cancellationToken = default)
    {
        var connection = await _accounts.SyncAsync(accountId, cancellationToken);

        return connection is null
            ? WhatsAppConnectionResponse.Disconnected()
            : connection.ToResponse().WithExpiryApplied(_clock.UtcNow);
    }

    /// <inheritdoc />
    public async Task<PagedResult<MessageTemplateResponse>> SearchTemplatesAsync(
        TemplateQuery query,
        string? accountId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var filtered = _templates.Query()
            .Where(TemplateAccount.BelongsTo(await WabaIdAsync(accountId, cancellationToken)));

        if (Enum.TryParse<TemplateStatus>(query.Status, ignoreCase: true, out var status)
            && !string.Equals(query.Status, TemplateQuery.All, StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(template => template.Status == status);
        }

        if (Enum.TryParse<TemplateCategory>(query.Category, ignoreCase: true, out var category)
            && !string.Equals(query.Category, TemplateQuery.All, StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(template => template.Category == category);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();

            // Name and body, because an operator searching for "shipped" is remembering what the
            // message said rather than what it was called. Header and footer are included for the
            // same reason. Translated to SQL by EF, so the server returns a page rather than
            // everything - filtering here in memory would leave the work exactly where it was.
            filtered = filtered.Where(template =>
                EF.Functions.ILike(template.Name, $"%{term}%")
                || EF.Functions.ILike(template.BodyText, $"%{term}%")
                || (template.HeaderText != null && EF.Functions.ILike(template.HeaderText, $"%{term}%"))
                || (template.FooterText != null && EF.Functions.ILike(template.FooterText, $"%{term}%")));
        }

        var projected = filtered
            // Newest-updated first: a template someone just resubmitted is the one they are looking
            // for. CreatedOn stands in for rows never edited, which have no ModifiedOn.
            .OrderByDescending(template => template.ModifiedOn ?? template.CreatedOn)
            .ThenBy(template => template.Name)
            .Select(template => new
            {
                template.Id,
                template.Name,
                template.Category,
                template.Status,
                template.Language,
                template.HeaderText,
                template.BodyText,
                template.FooterText,
                template.Variables,
                template.Buttons,
                template.QualityScore,
                template.TimesUsed,
                template.ModifiedOn,
                template.CreatedOn,
                template.RejectionReason,
                template.HeaderKind,
                template.HeaderSampleId,
                template.BodyExamples,
                template.HeaderExample,
            });

        var page = await _queries.ToPagedAsync(projected, query.Page, query.PageSize, cancellationToken);
        var samples = await _headerSamples.DescribeAsync(page.Items.Select(row => row.HeaderSampleId), cancellationToken);

        return page.Map(row => new MessageTemplateResponse(
            PublicId.From(PublicId.Template, row.Id),
            row.Name,
            row.Category,
            row.Status,
            row.Language,
            row.HeaderText,
            row.BodyText,
            row.FooterText,
            row.Variables,
            row.Buttons,
            row.QualityScore,
            row.TimesUsed,
            row.ModifiedOn ?? row.CreatedOn,
            row.RejectionReason,
            row.HeaderKind,
            row.HeaderSampleId is { } sampleId && samples.TryGetValue(sampleId, out var sample) ? sample : null,
            row.BodyExamples,
            row.HeaderExample));
    }

    /// <inheritdoc />
    public async Task<TemplateStatusCountsResponse> CountTemplatesAsync(
        string? accountId = null,
        CancellationToken cancellationToken = default)
    {
        // One grouped query rather than five round trips. The tenant filter still applies, so this
        // counts the caller's templates and nobody else's.
        var counts = await _queries.ToListAsync(
            _templates.Query()
                .Where(TemplateAccount.BelongsTo(await WabaIdAsync(accountId, cancellationToken)))
                .GroupBy(template => template.Status)
                .Select(group => new { Status = group.Key, Count = group.Count() }),
            cancellationToken);

        int CountOf(TemplateStatus wanted) =>
            counts.FirstOrDefault(entry => entry.Status == wanted)?.Count ?? 0;

        return new TemplateStatusCountsResponse(
            counts.Sum(entry => entry.Count),
            CountOf(TemplateStatus.Approved),
            CountOf(TemplateStatus.Pending),
            CountOf(TemplateStatus.Rejected),
            CountOf(TemplateStatus.Paused));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MessageTemplateResponse>> GetApprovedTemplatesAsync(
        string? accountId = null,
        CancellationToken cancellationToken = default)
    {
        // Unpaged on purpose. A picker that offers "every approved template" cannot be built on a
        // page: PageSize is clamped to 100, so asking for a large page silently returns the first
        // hundred and hides the rest with no error anywhere. Only approved templates can be sent,
        // and Meta's own per-account ceiling keeps this list small.
        var all = await GetTemplatesAsync(accountId, cancellationToken);

        return [.. all.Where(template => template.Status == TemplateStatus.Approved)];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MessageTemplateResponse>> GetTemplatesAsync(
        string? accountId = null,
        CancellationToken cancellationToken = default)
    {
        var rows = await _queries.ToListAsync(
            _templates.Query()
                .Where(TemplateAccount.BelongsTo(await WabaIdAsync(accountId, cancellationToken)))
                .OrderBy(template => template.Name)
                .Select(template => new
                {
                    template.Id,
                    template.Name,
                    template.Category,
                    template.Status,
                    template.Language,
                    template.HeaderText,
                    template.BodyText,
                    template.FooterText,
                    template.Variables,
                    template.Buttons,
                    template.QualityScore,
                    template.TimesUsed,
                    template.ModifiedOn,
                    template.CreatedOn,
                    template.RejectionReason,
                    template.HeaderKind,
                    template.HeaderSampleId,
                    template.BodyExamples,
                    template.HeaderExample,
                }),
            cancellationToken);

        var samples = await _headerSamples.DescribeAsync(rows.Select(row => row.HeaderSampleId), cancellationToken);

        return [.. rows.Select(row => new MessageTemplateResponse(
            PublicId.From(PublicId.Template, row.Id),
            row.Name,
            row.Category,
            row.Status,
            row.Language,
            row.HeaderText,
            // Placeholders are returned verbatim; the client highlights {{1}} and friends, so
            // any normalisation here would break that rendering.
            row.BodyText,
            row.FooterText,
            row.Variables,
            row.Buttons,
            row.QualityScore,
            row.TimesUsed,
            row.ModifiedOn ?? row.CreatedOn,
            row.RejectionReason,
            row.HeaderKind,
            row.HeaderSampleId is { } sampleId && samples.TryGetValue(sampleId, out var sample) ? sample : null,
            row.BodyExamples,
            row.HeaderExample))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MessageTemplateResponse>> SyncTemplatesAsync(
        string? accountId = null,
        CancellationToken cancellationToken = default)
    {
        var connection = await _access.ResolveAsync(
            accountId,
            Common.Constants.ContractEnums.WhatsAppAccessLevel.View,
            cancellationToken);

        if (connection?.WabaId is not { Length: > 0 } wabaId)
        {
            throw new BusinessRuleException(
                "whatsapp_not_connected",
                "Connect a WhatsApp account before syncing templates.");
        }

        var accessToken = connection.EncryptedAccessToken is { Length: > 0 } encrypted
            ? _protector.Unprotect(encrypted)
            : null;

        var remote = await _gateway.GetTemplatesAsync(wabaId, accessToken, cancellationToken);
        var tenantId = _tenantContext.RequireTenantId();

        var existing = await _queries.ToListAsync(_templates.Query(asNoTracking: false), cancellationToken);

        // Matched on name and language, which is what Meta treats as unique. Matching on Meta's id
        // alone would duplicate every template created locally before its first sync.
        var returned = new HashSet<MessageTemplate>(ReferenceEqualityComparer.Instance);

        foreach (var template in remote)
        {
            bool Same(MessageTemplate entry) =>
                string.Equals(entry.Name, template.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Language, template.Language, StringComparison.OrdinalIgnoreCase);

            // This business account's own copy first, then one nobody has claimed. A template with
            // the same name on another account is a different template at Meta - two numbers on
            // two accounts can each have an "order_update" - so it is never taken over.
            var local = existing.FirstOrDefault(entry =>
                            string.Equals(entry.WabaId, wabaId, StringComparison.Ordinal) && Same(entry))
                        ?? existing.FirstOrDefault(entry =>
                            entry.WabaId is null && !returned.Contains(entry) && Same(entry));

            if (local is null)
            {
                local = new MessageTemplate
                {
                    TenantId = tenantId,
                    Name = template.Name,
                    Language = template.Language,
                    BodyText = string.Empty,
                };

                _templates.Add(local);
            }

            local.MetaTemplateId = template.Id;

            // Claimed by the account it was just read from. A template carried over from another
            // account - Meta's test number, most often - moves here only if this account has it too.
            local.WabaId = wabaId;
            returned.Add(local);

            local.Status = Enum.TryParse<TemplateStatus>(template.Status, true, out var status)
                ? status
                : TemplateStatus.Pending;
            local.Category = Enum.TryParse<TemplateCategory>(template.Category, true, out var category)
                ? category
                : local.Category;

            // What Meta says the header is, so a template made in WhatsApp Manager with an image
            // header asks the campaign for an image like one made here does.
            local.HeaderKind = template.HeaderFormat?.ToUpperInvariant() switch
            {
                "TEXT" => TemplateHeaderKind.Text,
                "IMAGE" => TemplateHeaderKind.Image,
                "VIDEO" => TemplateHeaderKind.Video,
                "DOCUMENT" => TemplateHeaderKind.Document,
                null => TemplateHeaderKind.None,
                _ => local.HeaderKind,
            };
        }

        // Templates Meta no longer lists for this account are not deleted - a sync that removed a
        // customer's work over a paging hiccup would be far worse than a stale row - but they stop
        // counting as this account's. Their account is cleared, so they drop out of the lists and the
        // campaign picker, and the next sync that sees them again claims them back.
        foreach (var stale in existing.Where(entry =>
                     string.Equals(entry.WabaId, wabaId, StringComparison.Ordinal) && !returned.Contains(entry)))
        {
            stale.WabaId = null;
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await GetTemplatesAsync(accountId, cancellationToken);
    }

    /// <summary>
    /// The business account whose templates a number uses, or null when the workspace has none.
    /// </summary>
    /// <remarks>
    /// Requires view on the number, so an employee cannot list the templates of a number they
    /// cannot see by naming it.
    /// </remarks>
    private async Task<string?> WabaIdAsync(string? accountId, CancellationToken cancellationToken) =>
        (await _access.ResolveAsync(
            accountId,
            Common.Constants.ContractEnums.WhatsAppAccessLevel.View,
            cancellationToken))?.WabaId;
}
