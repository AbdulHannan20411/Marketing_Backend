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
    /// <summary>Returns the connection, or a disconnected placeholder when there is none.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<WhatsAppConnectionResponse> GetConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>Refreshes the connection from Meta and returns the updated state.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<WhatsAppConnectionResponse> SyncConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns every template.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<MessageTemplateResponse>> GetTemplatesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Returns a filtered, searched page of templates.</summary>
    /// <param name="query">Search, filters and paging.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PagedResult<MessageTemplateResponse>> SearchTemplatesAsync(
        TemplateQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Counts templates by approval state, ignoring any active filter.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<TemplateStatusCountsResponse> CountTemplatesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Returns every approved template, unpaged, for the campaign picker.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<MessageTemplateResponse>> GetApprovedTemplatesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Refreshes templates from Meta and returns them.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<MessageTemplateResponse>> SyncTemplatesAsync(
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IWhatsAppService" />
public sealed class WhatsAppService : IWhatsAppService
{
    private readonly IRepository<WhatsAppConnection> _connections;
    private readonly IRepository<MessageTemplate> _templates;
    private readonly IQueryExecutor _queries;
    private readonly IWhatsAppGateway _gateway;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISecretProtector _protector;
    private readonly ITenantContext _tenantContext;
    private readonly IDateTimeProvider _clock;

    /// <summary>Initialises a new instance.</summary>
    public WhatsAppService(
        IRepository<WhatsAppConnection> connections,
        IRepository<MessageTemplate> templates,
        IQueryExecutor queries,
        IWhatsAppGateway gateway,
        IUnitOfWork unitOfWork,
        ISecretProtector protector,
        ITenantContext tenantContext,
        IDateTimeProvider clock)
    {
        _connections = connections;
        _templates = templates;
        _queries = queries;
        _gateway = gateway;
        _unitOfWork = unitOfWork;
        _protector = protector;
        _tenantContext = tenantContext;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<WhatsAppConnectionResponse> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        // Loaded as an entity rather than projected in SQL: the onboarding steps are a JSON
        // document on the row, and the derived "still running" flag the client polls on cannot be
        // built by the database.
        var connection = await _queries.FirstOrDefaultAsync(
            _connections.Query(),
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
    public async Task<WhatsAppConnectionResponse> SyncConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _queries.FirstOrDefaultAsync(
            _connections.Query(asNoTracking: false),
            cancellationToken);

        if (connection?.PhoneNumberId is not { Length: > 0 } phoneNumberId)
        {
            return WhatsAppConnectionResponse.Disconnected();
        }

        // Passed explicitly, like every other outbound call. A Super Admin refreshing on a
        // customer's behalf carries the tenant in an explicit scope rather than in their own claims,
        // and the handler that would otherwise supply the token resolves the tenant in a scope that
        // cannot see it - so it sends none and Meta refuses the call.
        var accessToken = connection.EncryptedAccessToken is { Length: > 0 } encrypted
            ? _protector.Unprotect(encrypted)
            : null;

        var number = await _gateway.GetPhoneNumberAsync(phoneNumberId, accessToken, cancellationToken);

        connection.DisplayPhoneNumber = number.DisplayPhoneNumber;
        connection.VerifiedName = number.VerifiedName ?? connection.VerifiedName;
        connection.QualityRating = Enum.TryParse<QualityRating>(number.QualityRating, true, out var rating)
            ? rating
            : connection.QualityRating;

        // A successful round trip is itself the evidence the connection works, so the status is
        // corrected here rather than left at whatever it was when it last failed.
        connection.Status = ConnectionStatus.Connected;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await GetConnectionAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PagedResult<MessageTemplateResponse>> SearchTemplatesAsync(
        TemplateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var filtered = _templates.Query();

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
            });

        var page = await _queries.ToPagedAsync(projected, query.Page, query.PageSize, cancellationToken);

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
            row.RejectionReason));
    }

    /// <inheritdoc />
    public async Task<TemplateStatusCountsResponse> CountTemplatesAsync(
        CancellationToken cancellationToken = default)
    {
        // One grouped query rather than five round trips. The tenant filter still applies, so this
        // counts the caller's templates and nobody else's.
        var counts = await _queries.ToListAsync(
            _templates.Query()
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
        CancellationToken cancellationToken = default)
    {
        // Unpaged on purpose. A picker that offers "every approved template" cannot be built on a
        // page: PageSize is clamped to 100, so asking for a large page silently returns the first
        // hundred and hides the rest with no error anywhere. Only approved templates can be sent,
        // and Meta's own per-account ceiling keeps this list small.
        var all = await GetTemplatesAsync(cancellationToken);

        return [.. all.Where(template => template.Status == TemplateStatus.Approved)];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MessageTemplateResponse>> GetTemplatesAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _queries.ToListAsync(
            _templates.Query()
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
                }),
            cancellationToken);

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
            row.RejectionReason))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MessageTemplateResponse>> SyncTemplatesAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = await _queries.FirstOrDefaultAsync(_connections.Query(), cancellationToken);

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
        foreach (var template in remote)
        {
            var local = existing.FirstOrDefault(entry =>
                string.Equals(entry.Name, template.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Language, template.Language, StringComparison.OrdinalIgnoreCase));

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
            local.Status = Enum.TryParse<TemplateStatus>(template.Status, true, out var status)
                ? status
                : TemplateStatus.Pending;
            local.Category = Enum.TryParse<TemplateCategory>(template.Category, true, out var category)
                ? category
                : local.Category;
        }

        // Local templates Meta no longer knows about are left alone rather than deleted. A sync
        // that silently removes a customer's work because of a transient paging problem is far
        // worse than a stale row.
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await GetTemplatesAsync(cancellationToken);
    }
}
