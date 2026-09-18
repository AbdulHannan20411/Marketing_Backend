using Asp.Versioning;
using Marketing.API.Filters;
using Marketing.Application.DTOs.Campaigns;
using Marketing.Application.DTOs.WhatsApp;
using Marketing.Application.Interfaces;
using Marketing.Application.Services;
using Marketing.Common.Constants;
using Marketing.Common.Requests;
using Marketing.Common.Responses;
using Marketing.Infrastructure.WhatsApp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Marketing.API.Controllers;

/// <summary>Meta WhatsApp Business Account connection for the resolved tenant.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/whatsapp")]
[Authorize]
public sealed class WhatsAppController : ApiControllerBase
{
    private readonly IWhatsAppService _whatsApp;
    private readonly IWhatsAppConnectionService _connections;
    private readonly Application.Services.WhatsApp.IMediaService _media;
    private readonly ITenantScopeResolver _scope;
    private readonly WhatsAppOptions _options;

    /// <summary>Initialises a new instance.</summary>
    public WhatsAppController(
        IWhatsAppService whatsApp,
        IWhatsAppConnectionService connections,
        Application.Services.WhatsApp.IMediaService media,
        ITenantScopeResolver scope,
        IOptions<WhatsAppOptions> options)
    {
        _whatsApp = whatsApp;
        _connections = connections;
        _media = media;
        _scope = scope;
        _options = options.Value;
    }

    /// <summary>Returns the identifiers needed to open the Embedded Signup dialog.</summary>
    /// <remarks>
    /// Read-only and free of tenant data, so it takes no <c>adminId</c>. It is still behind the
    /// connect permission: a user who cannot connect an account has no reason to be handed the
    /// means of opening the dialog.
    /// </remarks>
    /// <response code="200">The signup parameters.</response>
    [HttpGet("signup-config")]
    [RequirePermission(Permissions.WhatsApp.Connect)]
    [ProducesResponseType(typeof(ApiResponse<SignupConfigResponse>), StatusCodes.Status200OK)]
    public IActionResult GetSignupConfig() =>
        Success(new SignupConfigResponse(_options.AppId, _options.ConfigId, _options.ApiVersion));

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

    /// <summary>Retries a connection whose onboarding stopped part-way.</summary>
    /// <remarks>
    /// Uses the credential already stored, so it needs no new authorisation code. A failure at
    /// subscribe, register or profile is not a credential problem - the token was exchanged before
    /// any of them ran - and the code from signup is single use, so without this the only recovery
    /// is the whole Meta popup again.
    /// <para>
    /// Returns immediately with <c>pending</c>; the poller does the work and the client watches the
    /// steps, exactly as it does after a first connection.
    /// </para>
    /// </remarks>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Onboarding restarted; poll the connection for progress.</response>
    /// <response code="404">Nothing is connected.</response>
    /// <response code="409">
    /// The stored credential was itself refused, so a retry cannot help. Connect again instead.
    /// </response>
    [HttpPost("connect/resume")]
    [RequirePermission(Permissions.WhatsApp.Connect)]
    [ProducesResponseType(typeof(ApiResponse<WhatsAppConnectionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ResumeAsync(
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(
            await _connections.ResumeOnboardingAsync(cancellationToken),
            "Retrying the connection.");
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

    /// <summary>
    /// Stores a file and uploads it to Meta, returning the handle a message can reference.
    /// </summary>
    /// <remarks>
    /// The browser never uploads to Meta directly: the credential that would authorise it must not
    /// leave the server, and the stored copy is what keeps an old thread renderable after Meta's own
    /// copy expires.
    /// </remarks>
    /// <param name="request">The file and what kind it is.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The stored file.</response>
    /// <response code="409">No WhatsApp account is connected.</response>
    /// <response code="422">The file is of a type or size WhatsApp refuses.</response>
    [HttpPost("media")]
    [RequirePermission(Permissions.WhatsApp.InboxReply, Permissions.WhatsApp.CampaignsCreate)]
    [RequestSizeLimit(MediaLimits.LargestUploadBytes)]
    [ProducesResponseType(typeof(ApiResponse<MediaAssetResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> UploadMediaAsync(
        [FromForm] WhatsAppMediaUploadRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        if (request.File is not { Length: > 0 } file)
        {
            throw new Common.Exceptions.ValidationException("file", "Choose a file to upload.");
        }

        if (!Enum.TryParse<ContractEnums.MediaKind>(request.Kind, ignoreCase: true, out var kind))
        {
            throw new Common.Exceptions.ValidationException(
                "kind",
                "Kind must be image, video, document or audio.");
        }

        await using var content = file.OpenReadStream();

        var media = await _media.UploadAsync(
            new MediaUploadCommand(kind, file.FileName, file.ContentType, file.Length, content),
            cancellationToken);

        return Success(media, "File uploaded.");
    }

    /// <summary>Streams a stored file back.</summary>
    /// <remarks>
    /// Serves this platform's own copy, not Meta's URL - which expires after 30 days and would let
    /// anyone holding the link read another workspace's attachment. An id belonging to a different
    /// workspace is not found rather than forbidden.
    /// </remarks>
    /// <param name="id">Public media identifier.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The bytes.</response>
    /// <response code="404">No such file in this workspace.</response>
    [HttpGet("media/{id}")]
    [RequirePermission(Permissions.WhatsApp.InboxView, Permissions.WhatsApp.TemplatesView)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMediaAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var download = await _media.OpenAsync(id, cancellationToken);

        // No file name on the response: the client fetches this as a blob and renders it inline, and
        // a download disposition would turn every preview into a save prompt.
        return File(download.Content, download.ContentType);
    }
}

/// <summary>The multipart body of a media upload.</summary>
public sealed class WhatsAppMediaUploadRequest
{
    /// <summary>The file.</summary>
    public IFormFile? File { get; init; }

    /// <summary>What it is: <c>image</c>, <c>video</c>, <c>document</c> or <c>audio</c>.</summary>
    public string? Kind { get; init; }
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

    /// <summary>Returns a searched, filtered page of templates.</summary>
    /// <remarks>
    /// Filtering and paging happen in SQL. Cleared filters arrive as the literal <c>all</c>, so
    /// "parameter absent" and "filter cleared" are the same request rather than two code paths.
    /// </remarks>
    /// <param name="query">Search, filters and paging.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The matching page.</response>
    [HttpGet]
    [RequirePermission(Permissions.WhatsApp.TemplatesView)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<MessageTemplateResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync(
        [FromQuery] TemplateQuery query,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _whatsApp.SearchTemplatesAsync(query, cancellationToken));
    }

    /// <summary>Counts templates by approval state, across the whole collection.</summary>
    /// <remarks>
    /// Unfiltered on purpose. These render as badges on the status chips and have to stay still
    /// while an operator clicks between them; counts that answered the active filter would show the
    /// selected chip's number and every other at zero.
    /// </remarks>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The counts.</response>
    [HttpGet("counts")]
    [RequirePermission(Permissions.WhatsApp.TemplatesView)]
    [ProducesResponseType(typeof(ApiResponse<TemplateStatusCountsResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCountsAsync(
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _whatsApp.CountTemplatesAsync(cancellationToken));
    }

    /// <summary>Returns every approved template, unpaged, for the campaign picker.</summary>
    /// <remarks>
    /// Deliberately not a page. <c>PageSize</c> is clamped to 100, so a picker built on a large page
    /// request would silently omit the hundred-and-first template with no error to notice.
    /// </remarks>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The approved templates.</response>
    [HttpGet("approved")]
    [RequirePermission(Permissions.WhatsApp.TemplatesView)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<MessageTemplateResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetApprovedAsync(
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _whatsApp.GetApprovedTemplatesAsync(cancellationToken));
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

    /// <summary>Returns one campaign, with its recurrence rule and audience.</summary>
    /// <param name="id">Campaign identifier.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The campaign.</response>
    /// <response code="404">No such campaign.</response>
    [HttpGet("{id}")]
    [RequirePermission(Permissions.WhatsApp.CampaignsReports, Permissions.WhatsApp.CampaignsCreate)]
    [ProducesResponseType(typeof(ApiResponse<CampaignResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByIdAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _campaigns.GetCampaignAsync(id, cancellationToken));
    }

    /// <summary>Returns a campaign's firings, newest first.</summary>
    /// <remarks>
    /// A recurring campaign is many sends, not one. The campaign's own counters are lifetime totals
    /// across every run; these are the counters for each firing on its own.
    /// </remarks>
    /// <param name="id">Campaign identifier.</param>
    /// <param name="request">Paging.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The runs.</response>
    [HttpGet("{id}/runs")]
    [RequirePermission(Permissions.WhatsApp.CampaignsReports, Permissions.WhatsApp.CampaignsCreate)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<CampaignRunResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRunsAsync(
        string id,
        [FromQuery] PageRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _campaigns.GetRunsAsync(id, request, cancellationToken));
    }
}
