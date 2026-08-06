using Marketing.Application.DTOs.WhatsApp;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;

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

    /// <summary>Initialises a new instance.</summary>
    public WhatsAppService(
        IRepository<WhatsAppConnection> connections,
        IRepository<MessageTemplate> templates,
        IQueryExecutor queries)
    {
        _connections = connections;
        _templates = templates;
        _queries = queries;
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
                entity.ConnectedAt,
                entity.WebhookHealthy,
                entity.TemplateNamespaceAlias)),
            cancellationToken);

        // Never a 404. "No number connected yet" is a normal state the client renders a connect
        // prompt from, whereas a 404 would put the screen into an error state.
        return connection ?? WhatsAppConnectionResponse.Disconnected();
    }

    /// <inheritdoc />
    public Task<WhatsAppConnectionResponse> SyncConnectionAsync(CancellationToken cancellationToken = default)
    {
        // NOTE: the Meta round trip is not wired up. It needs the per-tenant access token, which
        // arrives with the WhatsApp connection module - see the note in Directory.Packages.props
        // and CLAUDE.md. Returning stored state keeps the endpoint's shape honest in the meantime;
        // it does not yet refresh anything.
        return GetConnectionAsync(cancellationToken);
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
    public Task<IReadOnlyList<MessageTemplateResponse>> SyncTemplatesAsync(
        CancellationToken cancellationToken = default)
    {
        // NOTE: as above - returns stored templates rather than refreshing from Meta, pending the
        // per-tenant access token.
        return GetTemplatesAsync(cancellationToken);
    }
}
