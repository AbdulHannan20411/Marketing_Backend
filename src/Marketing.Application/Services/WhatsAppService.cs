using Marketing.Application.DTOs.WhatsApp;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
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
    private readonly ITenantContext _tenantContext;

    /// <summary>Initialises a new instance.</summary>
    public WhatsAppService(
        IRepository<WhatsAppConnection> connections,
        IRepository<MessageTemplate> templates,
        IQueryExecutor queries,
        IWhatsAppGateway gateway,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext)
    {
        _connections = connections;
        _templates = templates;
        _queries = queries;
        _gateway = gateway;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc />
    public async Task<WhatsAppConnectionResponse> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _queries.FirstOrDefaultAsync(
            _connections.Query().Select(entity => new WhatsAppConnectionResponse(
                entity.Status,
                entity.DisplayPhoneNumber,
                entity.VerifiedName,
                entity.BusinessProfileAbout,
                entity.BusinessCategory,
                entity.QualityRating,
                entity.MessagingLimit,
                entity.MessagesLast24h,
                entity.MessagingTier,
                entity.ConnectedAt,
                entity.WebhookHealthy,
                entity.TemplateNamespaceAlias)),
            cancellationToken);

        // Never a 404. "No number connected yet" is a normal state the client renders a connect
        // prompt from, whereas a 404 would put the screen into an error state.
        return connection ?? WhatsAppConnectionResponse.Disconnected();
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

        var number = await _gateway.GetPhoneNumberAsync(phoneNumberId, cancellationToken);

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

        var remote = await _gateway.GetTemplatesAsync(wabaId, cancellationToken);
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
