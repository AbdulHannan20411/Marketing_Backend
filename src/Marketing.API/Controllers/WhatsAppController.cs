using Asp.Versioning;
using Marketing.API.Filters;
using Marketing.Application.DTOs.Campaigns;
using Marketing.Application.DTOs.WhatsApp;
using Marketing.Application.Interfaces;
using Marketing.Application.Services;
using Marketing.Common.Constants;
using Marketing.Common.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Marketing.API.Controllers;

/// <summary>Meta WhatsApp Business Account connection for the resolved tenant.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/whatsapp")]
[Authorize]
public sealed class WhatsAppController : ApiControllerBase
{
    private readonly IWhatsAppService _whatsApp;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    public WhatsAppController(IWhatsAppService whatsApp, ITenantScopeResolver scope)
    {
        _whatsApp = whatsApp;
        _scope = scope;
    }

    /// <summary>Returns the connection.</summary>
    /// <remarks>
    /// A tenant with no connected number gets a <c>disconnected</c> connection rather than a 404,
    /// because the client renders a connect prompt from it.
    /// </remarks>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The connection state.</response>
    [HttpGet("connection")]
    [RequirePermission(Permissions.WhatsApp.TemplatesView, Permissions.WhatsApp.Connect)]
    [ProducesResponseType(typeof(ApiResponse<WhatsAppConnectionResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetConnectionAsync(
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _whatsApp.GetConnectionAsync(cancellationToken));
    }

    /// <summary>Refreshes the connection from Meta.</summary>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The refreshed connection state.</response>
    [HttpPost("connection/sync")]
    [RequirePermission(Permissions.WhatsApp.Connect)]
    [ProducesResponseType(typeof(ApiResponse<WhatsAppConnectionResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SyncConnectionAsync(
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(
            await _whatsApp.SyncConnectionAsync(cancellationToken),
            "WhatsApp connection refreshed.");
    }
}

/// <summary>Message templates for the resolved tenant.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/templates")]
[Authorize]
public sealed class TemplatesController : ApiControllerBase
{
    private readonly IWhatsAppService _whatsApp;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    public TemplatesController(IWhatsAppService whatsApp, ITenantScopeResolver scope)
    {
        _whatsApp = whatsApp;
        _scope = scope;
    }

    /// <summary>Returns every template.</summary>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The templates.</response>
    [HttpGet]
    [RequirePermission(Permissions.WhatsApp.TemplatesView)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<MessageTemplateResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync(
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _whatsApp.GetTemplatesAsync(cancellationToken));
    }

    /// <summary>Refreshes templates from Meta.</summary>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The refreshed templates.</response>
    [HttpPost("sync")]
    [RequirePermission(Permissions.WhatsApp.TemplatesSync)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<MessageTemplateResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SyncAsync(
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var templates = await _whatsApp.SyncTemplatesAsync(cancellationToken);

        return Success(templates, $"Synced {templates.Count} templates from Meta.");
    }
}

/// <summary>Campaigns for the resolved tenant.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/campaigns")]
[Authorize]
public sealed class CampaignsController : ApiControllerBase
{
    private readonly ICampaignService _campaigns;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    public CampaignsController(ICampaignService campaigns, ITenantScopeResolver scope)
    {
        _campaigns = campaigns;
        _scope = scope;
    }

    /// <summary>Returns every campaign, newest first.</summary>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The campaigns.</response>
    [HttpGet]
    [RequirePermission(Permissions.WhatsApp.CampaignsReports, Permissions.WhatsApp.CampaignsCreate)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<CampaignResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync(
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _campaigns.GetCampaignsAsync(cancellationToken));
    }
}
