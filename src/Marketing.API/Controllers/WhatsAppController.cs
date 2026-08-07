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
    private readonly IWhatsAppConnectionService _connections;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    public WhatsAppController(
        IWhatsAppService whatsApp,
        IWhatsAppConnectionService connections,
        ITenantScopeResolver scope)
    {
        _whatsApp = whatsApp;
        _connections = connections;
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

    /// <summary>Completes Meta Embedded Signup and links the chosen number.</summary>
    /// <remarks>
    /// The client sends the authorisation code Meta handed back; the exchange happens here because
    /// it needs the app secret. The code is single-use, so a retried request fails at Meta rather
    /// than silently reconnecting.
    /// </remarks>
    /// <param name="request">Signup callback values.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The connected account.</response>
    /// <response code="422">The number could not be verified with Meta.</response>
    [HttpPost("connect")]
    [RequirePermission(Permissions.WhatsApp.Connect)]
    [ProducesResponseType(typeof(ApiResponse<WhatsAppConnectionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ConnectAsync(
        [FromBody] ConnectWhatsAppRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(
            await _connections.ConnectAsync(request, cancellationToken),
            "WhatsApp Business Account connected.");
    }

    /// <summary>Links an account using a token supplied directly. Platform staff only.</summary>
    /// <remarks>
    /// An operator tool for exercising the messaging path before an app has passed Meta review.
    /// Restricted to the Super Admin role because a token pasted here has arrived through a channel
    /// nobody audited; tenant administrators onboard through Embedded Signup instead.
    /// </remarks>
    /// <param name="request">Token and account identifiers.</param>
    /// <param name="adminId">Tenant to connect on behalf of.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The connected account.</response>
    [HttpPost("connect/manual")]
    [Authorize(Roles = Roles.SuperAdmin)]
    [ProducesResponseType(typeof(ApiResponse<WhatsAppConnectionResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ConnectManuallyAsync(
        [FromBody] ManualConnectWhatsAppRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(
            await _connections.ConnectManuallyAsync(request, cancellationToken),
            "WhatsApp Business Account connected.");
    }

    /// <summary>Disconnects the account and destroys the stored token.</summary>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The disconnected state.</response>
    /// <response code="404">No account is connected.</response>
    [HttpPost("disconnect")]
    [RequirePermission(Permissions.WhatsApp.Disconnect)]
    [ProducesResponseType(typeof(ApiResponse<WhatsAppConnectionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DisconnectAsync(
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(
            await _connections.DisconnectAsync(cancellationToken),
            "WhatsApp Business Account disconnected.");
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
