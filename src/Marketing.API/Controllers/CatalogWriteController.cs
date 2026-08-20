using Asp.Versioning;
using Marketing.API.Filters;
using Marketing.Application.DTOs.Campaigns;
using Marketing.Application.DTOs.Contacts;
using Marketing.Application.DTOs.Platform;
using Marketing.Application.DTOs.WhatsApp;
using Marketing.Application.Interfaces;
using Marketing.Application.Services;
using Marketing.Common.Constants;
using Marketing.Common.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Marketing.API.Controllers;

/// <summary>Contact group writes.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/groups")]
[Authorize]
[RequireModule(PlanModules.Crm)]
public sealed class GroupWriteController : ApiControllerBase
{
    private readonly ICatalogService _catalog;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    public GroupWriteController(ICatalogService catalog, ITenantScopeResolver scope)
    {
        _catalog = catalog;
        _scope = scope;
    }

    /// <summary>Creates a group.</summary>
    /// <response code="200">The created group.</response>
    [HttpPost]
    [RequirePermission(Permissions.Contacts.GroupsManage)]
    [ProducesResponseType(typeof(ApiResponse<ContactGroupResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] ContactGroupDraft draft,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var group = await _catalog.CreateGroupAsync(draft, cancellationToken);

        return Success(group, $"Group \"{group.Name}\" created.");
    }

    /// <summary>Replaces a group.</summary>
    /// <response code="200">The updated group.</response>
    [HttpPut("{id}")]
    [RequirePermission(Permissions.Contacts.GroupsManage)]
    [ProducesResponseType(typeof(ApiResponse<ContactGroupResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateAsync(
        string id,
        [FromBody] ContactGroupDraft draft,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var group = await _catalog.UpdateGroupAsync(id, draft, cancellationToken);

        return Success(group, $"Group \"{group.Name}\" saved.");
    }

    /// <summary>Deletes a group. The contacts in it are not affected.</summary>
    /// <response code="200">The group was deleted.</response>
    [HttpDelete("{id}")]
    [RequirePermission(Permissions.Contacts.GroupsManage)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        await _catalog.DeleteGroupAsync(id, cancellationToken);

        return SuccessEmpty("Group deleted.");
    }
}

/// <summary>Tag writes.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/tags")]
[Authorize]
[RequireModule(PlanModules.Crm)]
public sealed class TagWriteController : ApiControllerBase
{
    private readonly ICatalogService _catalog;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    public TagWriteController(ICatalogService catalog, ITenantScopeResolver scope)
    {
        _catalog = catalog;
        _scope = scope;
    }

    /// <summary>Creates a tag.</summary>
    /// <response code="200">The created tag.</response>
    [HttpPost]
    [RequirePermission(Permissions.Contacts.TagsManage)]
    [ProducesResponseType(typeof(ApiResponse<ContactTagResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] ContactTagDraft draft,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var tag = await _catalog.CreateTagAsync(draft, cancellationToken);

        return Success(tag, $"Tag \"{tag.Name}\" created.");
    }

    /// <summary>Replaces a tag.</summary>
    /// <response code="200">The updated tag.</response>
    [HttpPut("{id}")]
    [RequirePermission(Permissions.Contacts.TagsManage)]
    [ProducesResponseType(typeof(ApiResponse<ContactTagResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateAsync(
        string id,
        [FromBody] ContactTagDraft draft,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var tag = await _catalog.UpdateTagAsync(id, draft, cancellationToken);

        return Success(tag, $"Tag \"{tag.Name}\" saved.");
    }

    /// <summary>Deletes a tag, removing it from every contact that carried it.</summary>
    /// <remarks>No contact is deleted. The message reports how many lost the label.</remarks>
    /// <response code="200">The tag was deleted.</response>
    [HttpDelete("{id}")]
    [RequirePermission(Permissions.Contacts.TagsManage)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var affected = await _catalog.DeleteTagAsync(id, cancellationToken);

        return SuccessEmpty($"Tag deleted from {affected} contacts.");
    }
}

/// <summary>Message template writes.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/templates")]
[Authorize]
public sealed class TemplateWriteController : ApiControllerBase
{
    private readonly ICatalogService _catalog;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    public TemplateWriteController(ICatalogService catalog, ITenantScopeResolver scope)
    {
        _catalog = catalog;
        _scope = scope;
    }

    /// <summary>Creates a template. It starts pending, because only Meta can approve one.</summary>
    /// <response code="200">The created template.</response>
    [HttpPost]
    [RequirePermission(Permissions.WhatsApp.TemplatesSync)]
    [ProducesResponseType(typeof(ApiResponse<MessageTemplateResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] MessageTemplateDraft draft,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var template = await _catalog.CreateTemplateAsync(draft, cancellationToken);

        return Success(template, $"Template \"{template.Name}\" created and awaiting Meta review.");
    }

    /// <summary>Replaces a template. Editing returns it to the pending state.</summary>
    /// <response code="200">The updated template.</response>
    [HttpPut("{id}")]
    [RequirePermission(Permissions.WhatsApp.TemplatesSync)]
    [ProducesResponseType(typeof(ApiResponse<MessageTemplateResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateAsync(
        string id,
        [FromBody] MessageTemplateDraft draft,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var template = await _catalog.UpdateTemplateAsync(id, draft, cancellationToken);

        return Success(template, $"Template \"{template.Name}\" saved and awaiting Meta review.");
    }

    /// <summary>Deletes a template.</summary>
    /// <response code="200">The template was deleted.</response>
    /// <response code="409">A scheduled or running campaign uses it.</response>
    [HttpDelete("{id}")]
    [RequirePermission(Permissions.WhatsApp.TemplatesSync)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        await _catalog.DeleteTemplateAsync(id, cancellationToken);

        return SuccessEmpty("Template deleted.");
    }
}

/// <summary>Campaign writes and lifecycle transitions.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/campaigns")]
[Authorize]
[EnableRateLimiting(AppConstants.RateLimits.Campaigns)]
public sealed class CampaignWriteController : ApiControllerBase
{
    private readonly ICampaignWriteService _campaigns;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    public CampaignWriteController(ICampaignWriteService campaigns, ITenantScopeResolver scope)
    {
        _campaigns = campaigns;
        _scope = scope;
    }

    /// <summary>Creates a campaign in the draft state.</summary>
    /// <response code="200">The created campaign.</response>
    /// <response code="409">The chosen template is not approved by Meta.</response>
    [HttpPost]
    [RequirePermission(Permissions.WhatsApp.CampaignsCreate)]
    [ProducesResponseType(typeof(ApiResponse<CampaignResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] CampaignDraft draft,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var campaign = await _campaigns.CreateAsync(draft, cancellationToken);

        return Success(campaign, $"Campaign \"{campaign.Name}\" created.");
    }

    /// <summary>Replaces a campaign. Only drafts and scheduled campaigns may be edited.</summary>
    /// <response code="200">The updated campaign.</response>
    /// <response code="409">The campaign has already started sending.</response>
    [HttpPut("{id}")]
    [RequirePermission(Permissions.WhatsApp.CampaignsEdit)]
    [ProducesResponseType(typeof(ApiResponse<CampaignResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateAsync(
        string id,
        [FromBody] CampaignDraft draft,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var campaign = await _campaigns.UpdateAsync(id, draft, cancellationToken);

        return Success(campaign, $"Campaign \"{campaign.Name}\" saved.");
    }

    /// <summary>Deletes a campaign.</summary>
    /// <response code="200">The campaign was deleted.</response>
    /// <response code="409">The campaign is running; cancel it first.</response>
    [HttpDelete("{id}")]
    [RequirePermission(Permissions.WhatsApp.CampaignsDelete)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        await _campaigns.DeleteAsync(id, cancellationToken);

        return SuccessEmpty("Campaign deleted.");
    }

    /// <summary>Schedules a campaign for a future dispatch.</summary>
    /// <response code="200">The scheduled campaign.</response>
    [HttpPost("{id}/schedule")]
    [RequirePermission(Permissions.WhatsApp.CampaignsSchedule)]
    [ProducesResponseType(typeof(ApiResponse<CampaignResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ScheduleAsync(
        string id,
        [FromBody] ScheduleCampaignRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var campaign = await _campaigns.ScheduleAsync(id, request, cancellationToken);

        return Success(campaign, $"Campaign scheduled for {campaign.ScheduledAt:g} UTC.");
    }

    /// <summary>Starts dispatching a campaign now.</summary>
    /// <remarks>
    /// Send an <c>Idempotency-Key</c> header. A campaign already sending or completed is returned
    /// unchanged rather than dispatched twice, so a retry after a timeout cannot double-send.
    /// </remarks>
    /// <response code="200">The campaign, now sending.</response>
    /// <response code="409">The campaign has no audience, or cannot move to sending.</response>
    [HttpPost("{id}/send")]
    [RequirePermission(Permissions.WhatsApp.CampaignsSend)]
    [ProducesResponseType(typeof(ApiResponse<CampaignResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SendAsync(
        string id,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var campaign = await _campaigns.SendAsync(id, idempotencyKey, cancellationToken);

        return Success(campaign, $"Campaign \"{campaign.Name}\" is sending to {campaign.Metrics.AudienceSize} contacts.");
    }

    /// <summary>Pauses a running campaign.</summary>
    /// <response code="200">The paused campaign.</response>
    [HttpPost("{id}/pause")]
    [RequirePermission(Permissions.WhatsApp.CampaignsPause)]
    [ProducesResponseType(typeof(ApiResponse<CampaignResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> PauseAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var campaign = await _campaigns.PauseAsync(id, cancellationToken);

        return Success(campaign, "Campaign paused.");
    }

    /// <summary>Cancels a scheduled, running or paused campaign.</summary>
    /// <response code="200">The cancelled campaign.</response>
    [HttpPost("{id}/cancel")]
    [RequirePermission(Permissions.WhatsApp.CampaignsCancel)]
    [ProducesResponseType(typeof(ApiResponse<CampaignResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> CancelAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var campaign = await _campaigns.CancelAsync(id, cancellationToken);

        return Success(campaign, "Campaign cancelled.");
    }

    /// <summary>Copies a campaign into a new draft.</summary>
    /// <remarks>
    /// The recurrence rule is copied; counters, run history and timestamps are not, because none of
    /// them are true of a campaign that has not run.
    /// </remarks>
    /// <response code="200">The new draft.</response>
    [HttpPost("{id}/duplicate")]
    [RequirePermission(Permissions.WhatsApp.CampaignsCreate)]
    [ProducesResponseType(typeof(ApiResponse<CampaignResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> DuplicateAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var campaign = await _campaigns.DuplicateAsync(id, cancellationToken);

        return Success(campaign, $"Campaign copied to \"{campaign.Name}\".");
    }

    /// <summary>Resumes a paused campaign.</summary>
    /// <remarks>
    /// A recurring campaign's next occurrence is recomputed from now rather than restored, so one
    /// paused for three weeks does not wake up owing three sends.
    /// </remarks>
    /// <response code="200">The resumed campaign.</response>
    /// <response code="409">The campaign is not paused.</response>
    [HttpPost("{id}/resume")]
    [RequirePermission(Permissions.WhatsApp.CampaignsPause)]
    [ProducesResponseType(typeof(ApiResponse<CampaignResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ResumeAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var campaign = await _campaigns.ResumeAsync(id, cancellationToken);

        return Success(campaign, "Campaign resumed.");
    }

    /// <summary>Runs a scheduled campaign immediately, without disturbing its schedule.</summary>
    /// <remarks>
    /// A campaign set for Monday can be run today and still runs on Monday. The extra firing does
    /// not consume an "after N occurrences" allowance.
    /// <para>
    /// A run already in flight is returned rather than a second one started, so a double-clicked
    /// button cannot send to the whole audience twice.
    /// </para>
    /// </remarks>
    /// <response code="200">The run that was started.</response>
    /// <response code="409">The campaign is not scheduled, or has no audience.</response>
    [HttpPost("{id}/run-now")]
    [RequirePermission(Permissions.WhatsApp.CampaignsSend)]
    [ProducesResponseType(typeof(ApiResponse<CampaignRunResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RunNowAsync(
        string id,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var run = await _campaigns.RunNowAsync(id, idempotencyKey, cancellationToken);

        return Success(run, "Campaign is running now. Its schedule is unchanged.");
    }

    /// <summary>Counts the distinct, contactable audience across a set of groups.</summary>
    /// <remarks>
    /// Deduplicated across groups and excluding unsubscribed contacts, so the wizard can show a real
    /// number rather than a sum that double-counts anyone in two groups.
    /// </remarks>
    /// <response code="200">The recipient count.</response>
    [HttpPost("preview-audience")]
    [RequirePermission(Permissions.WhatsApp.CampaignsCreate)]
    [ProducesResponseType(typeof(ApiResponse<PreviewAudienceResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> PreviewAudienceAsync(
        [FromBody] PreviewAudienceRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _campaigns.PreviewAudienceAsync(request, cancellationToken));
    }
}

/// <summary>Admin account administration. Platform staff only.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/superadmin/admins")]
[Authorize(Policy = AppConstants.Policies.SuperAdminOnly)]
[EnableRateLimiting(AppConstants.RateLimits.Admin)]
public sealed class AdminAccountController : ApiControllerBase
{
    private readonly IAdminAccountService _accounts;

    /// <summary>Initialises a new instance.</summary>
    public AdminAccountController(IAdminAccountService accounts) => _accounts = accounts;

    /// <summary>Creates an organisation and its first administrator.</summary>
    /// <response code="200">The created account.</response>
    /// <response code="409">The address or organisation name is already in use.</response>
    [HttpPost]
    [RequirePermission(Permissions.Platform.Tenants)]
    [ProducesResponseType(typeof(ApiResponse<AdminAccount>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] CreateAdminAccountRequest request,
        CancellationToken cancellationToken)
    {
        var account = await _accounts.CreateAsync(request, cancellationToken);

        return Success(account, $"{account.Organisation} created with {account.Email} as administrator.");
    }

    /// <summary>Updates an Admin account. Omitted fields are left unchanged.</summary>
    /// <response code="200">The updated account.</response>
    [HttpPut("{id}")]
    [RequirePermission(Permissions.Platform.Tenants)]
    [ProducesResponseType(typeof(ApiResponse<AdminAccount>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateAsync(
        string id,
        [FromBody] UpdateAdminAccountRequest request,
        CancellationToken cancellationToken)
    {
        var account = await _accounts.UpdateAsync(id, request, cancellationToken);

        return Success(account, $"{account.Organisation} saved.");
    }

    /// <summary>Suspends or reactivates an Admin account and its organisation.</summary>
    /// <remarks>Suspending ends the administrator's live sessions immediately.</remarks>
    /// <response code="200">The updated account.</response>
    [HttpPut("{id}/status")]
    [RequirePermission(Permissions.Platform.Tenants)]
    [ProducesResponseType(typeof(ApiResponse<AdminAccount>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateStatusAsync(
        string id,
        [FromBody] UpdateAdminStatusRequest request,
        CancellationToken cancellationToken)
    {
        var account = await _accounts.UpdateStatusAsync(id, request, cancellationToken);

        return Success(account, $"{account.Organisation} is now {account.Status}.");
    }

    /// <summary>Removes an Admin account and suspends its organisation.</summary>
    /// <remarks>
    /// The organisation's data is retained. Deleting a customer's contacts, campaigns and billing
    /// history is a separate decision with a retention period attached to it.
    /// </remarks>
    /// <response code="200">The account was removed.</response>
    [HttpDelete("{id}")]
    [RequirePermission(Permissions.Platform.Tenants)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await _accounts.DeleteAsync(id, cancellationToken);

        return SuccessEmpty("Administrator removed and organisation suspended.");
    }
}
