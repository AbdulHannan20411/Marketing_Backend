using Marketing.Application.DTOs.WhatsApp;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Exceptions;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.WhatsApp;

/// <summary>A workspace's automatic-reply rules, and the allowance its plan gives them.</summary>
public interface IAutoReplyService
{
    /// <summary>Reads the rules, what the plan allows, and what is left of the allowance.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<AutoReplySettingsResponse> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves the rules, refusing anything the plan does not sell.</summary>
    /// <param name="request">The new rules.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<AutoReplySettingsResponse> UpdateAsync(
        AutoReplySettingsRequest request,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAutoReplyService" />
public sealed class AutoReplyService : IAutoReplyService
{
    /// <summary>Shortest and longest pause before a greeting reply, in seconds.</summary>
    private const int MinimumDelaySeconds = 10;
    private const int MaximumDelaySeconds = 1800;

    /// <summary>Shortest and longest wait before answering an unanswered message, in minutes.</summary>
    private const int MinimumUnansweredMinutes = 5;
    private const int MaximumUnansweredMinutes = 1440;

    private const int MaximumPerConversationPerDay = 20;
    private const int InstructionsMaxLength = 2000;

    private readonly IRepository<AutoReplySettings> _settings;
    private readonly IAutoReplyAllowance _allowance;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAiService _ai;
    private readonly ITenantContext _tenantContext;

    /// <summary>Initialises a new instance.</summary>
    public AutoReplyService(
        IRepository<AutoReplySettings> settings,
        IAutoReplyAllowance allowance,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        IAiService ai,
        ITenantContext tenantContext)
    {
        _settings = settings;
        _allowance = allowance;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _ai = ai;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc />
    public async Task<AutoReplySettingsResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantContext.RequireTenantId();
        var settings = await FindAsync(tracked: false, cancellationToken) ?? Defaults(tenantId);
        var allowance = await _allowance.ForTenantAsync(tenantId, cancellationToken);

        return ToResponse(settings, allowance);
    }

    /// <inheritdoc />
    public async Task<AutoReplySettingsResponse> UpdateAsync(
        AutoReplySettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tenantId = _tenantContext.RequireTenantId();
        var allowance = await _allowance.ForTenantAsync(tenantId, cancellationToken);
        var wanted = AutoReplyTriggers.Collapse(request.Triggers);

        Validate(request, wanted, allowance);

        var settings = await FindAsync(tracked: true, cancellationToken);

        if (settings is null)
        {
            settings = Defaults(tenantId);

            _settings.Add(settings);
        }

        settings.Enabled = request.Enabled;
        settings.GreetingEnabled = wanted.Contains(AutoReplyTriggers.Greeting, StringComparer.Ordinal);
        settings.FirstMessageEnabled = wanted.Contains(AutoReplyTriggers.FirstMessage, StringComparer.Ordinal);
        settings.UnansweredEnabled = wanted.Contains(AutoReplyTriggers.Unanswered, StringComparer.Ordinal);
        settings.DelaySeconds = request.DelaySeconds;
        settings.UnansweredAfterMinutes = request.UnansweredAfterMinutes;
        settings.Instructions = request.Instructions?.Trim() ?? string.Empty;
        settings.MaxPerConversationPerDay = request.MaxPerConversationPerDay;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ToResponse(settings, allowance);
    }

    /// <summary>Checks the rules against Meta's limits, the plan, and plain sense.</summary>
    private static void Validate(
        AutoReplySettingsRequest request,
        IReadOnlyList<string> wanted,
        AutoReplyAllowance allowance)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request.DelaySeconds is < MinimumDelaySeconds or > MaximumDelaySeconds)
        {
            errors["delaySeconds"] =
                [$"Wait between {MinimumDelaySeconds} seconds and {MaximumDelaySeconds / 60} minutes."];
        }

        if (request.UnansweredAfterMinutes is < MinimumUnansweredMinutes or > MaximumUnansweredMinutes)
        {
            errors["unansweredAfterMinutes"] =
                [$"Wait between {MinimumUnansweredMinutes} minutes and {MaximumUnansweredMinutes / 60} hours."];
        }

        if (request.MaxPerConversationPerDay is < 1 or > MaximumPerConversationPerDay)
        {
            errors["maxPerConversationPerDay"] = [$"Allow between 1 and {MaximumPerConversationPerDay} a day."];
        }

        if (request.Instructions is { Length: > InstructionsMaxLength })
        {
            errors["instructions"] = [$"Keep guidance to {InstructionsMaxLength} characters or fewer."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        // Refused rather than quietly ignored. An admin who switches something on and finds it off
        // again after a reload learns nothing; this names the plan as the reason.
        var unsold = wanted.Where(trigger => !allowance.AllowsTrigger(trigger)).ToList();

        if (unsold.Count > 0)
        {
            throw new BusinessRuleException(
                "auto_reply_trigger_not_in_plan",
                $"The {allowance.PlanName} plan does not include automatic replies for "
                + $"{string.Join(", ", unsold.Select(Describe))}. Upgrade to switch this on.");
        }
    }

    private static string Describe(string trigger) => trigger switch
    {
        AutoReplyTriggers.Greeting => "greetings",
        AutoReplyTriggers.FirstMessage => "first messages",
        AutoReplyTriggers.Unanswered => "unanswered messages",
        _ => trigger,
    };

    private async Task<AutoReplySettings?> FindAsync(bool tracked, CancellationToken cancellationToken) =>
        await _queries.FirstOrDefaultAsync(_settings.Query(asNoTracking: !tracked), cancellationToken);

    /// <summary>
    /// The rules a workspace has before anyone configures them: everything off.
    /// </summary>
    /// <remarks>
    /// Not persisted until an admin saves. A row written on first read would mean every workspace that
    /// merely opened the screen now owns settings, which makes "never configured" indistinguishable
    /// from "configured and switched off".
    /// </remarks>
    private static AutoReplySettings Defaults(long tenantId) => new() { TenantId = tenantId };

    private AutoReplySettingsResponse ToResponse(AutoReplySettings settings, AutoReplyAllowance allowance)
    {
        var chosen = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [AutoReplyTriggers.Greeting] = settings.GreetingEnabled,
            [AutoReplyTriggers.FirstMessage] = settings.FirstMessageEnabled,
            [AutoReplyTriggers.Unanswered] = settings.UnansweredEnabled,
        };

        return new AutoReplySettingsResponse(
            settings.Enabled,
            chosen,
            AutoReplyTriggers.Expand(allowance.AllowedTriggers),
            settings.DelaySeconds,
            settings.UnansweredAfterMinutes,
            settings.Instructions,
            settings.MaxPerConversationPerDay,
            allowance.MonthlyLimit,
            allowance.UsedThisPeriod,
            allowance.Remaining,
            allowance.PeriodEndsAt,
            _ai.IsConfigured);
    }
}

/// <summary>What a workspace's plan allows it to send automatically, and what it has used.</summary>
public interface IAutoReplyAllowance
{
    /// <summary>Reads the allowance for one workspace.</summary>
    /// <param name="tenantId">Workspace to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<AutoReplyAllowance> ForTenantAsync(long tenantId, CancellationToken cancellationToken = default);
}

/// <summary>A plan's automatic-reply entitlement, measured against this period's usage.</summary>
/// <param name="PlanName">Plan the workspace is on, for messages an admin reads.</param>
/// <param name="HasAiModule">Whether the plan includes the assistant at all.</param>
/// <param name="AllowedTriggers">Occasions the plan sells.</param>
/// <param name="MonthlyLimit">Replies allowed per period, or null for no ceiling.</param>
/// <param name="UsedThisPeriod">Replies already sent this period.</param>
/// <param name="PeriodEndsAt">When the allowance resets.</param>
public sealed record AutoReplyAllowance(
    string PlanName,
    bool HasAiModule,
    IReadOnlyList<string> AllowedTriggers,
    int? MonthlyLimit,
    int UsedThisPeriod,
    DateTimeOffset? PeriodEndsAt)
{
    /// <summary>What is left of the allowance, or null when there is no ceiling.</summary>
    public int? Remaining => MonthlyLimit is { } limit ? Math.Max(0, limit - UsedThisPeriod) : null;

    /// <summary>Whether another reply may be sent.</summary>
    public bool HasHeadroom => Remaining is null or > 0;

    /// <summary>Whether the plan sells a given trigger, and includes the assistant at all.</summary>
    /// <param name="trigger">Trigger key.</param>
    public bool AllowsTrigger(string trigger) =>
        HasAiModule && AllowedTriggers.Contains(trigger, StringComparer.OrdinalIgnoreCase);
}

/// <inheritdoc cref="IAutoReplyAllowance" />
/// <remarks>
/// Reads across tenants deliberately: the dispatcher runs on a schedule with no signed-in user, so it
/// asks for one workspace's entitlement at a time rather than relying on an ambient tenant.
/// </remarks>
public sealed class AutoReplyAllowanceReader : IAutoReplyAllowance
{
    private readonly IRepository<TenantSubscription> _subscriptions;
    private readonly IRepository<ConversationMessage> _messages;
    private readonly IQueryExecutor _queries;
    private readonly IDateTimeProvider _clock;

    /// <summary>Initialises a new instance.</summary>
    public AutoReplyAllowanceReader(
        IRepository<TenantSubscription> subscriptions,
        IRepository<ConversationMessage> messages,
        IQueryExecutor queries,
        IDateTimeProvider clock)
    {
        _subscriptions = subscriptions;
        _messages = messages;
        _queries = queries;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<AutoReplyAllowance> ForTenantAsync(long tenantId, CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;

        var plan = await _queries.FirstOrDefaultAsync(
            _subscriptions.Query()
                .IgnoreQueryFilters()
                .Where(subscription => !subscription.IsDeleted && subscription.TenantId == tenantId)
                .Select(subscription => new
                {
                    subscription.SubscriptionPlan.Name,
                    subscription.SubscriptionPlan.EnabledModules,
                    subscription.SubscriptionPlan.AutoReplyTriggers,
                    subscription.SubscriptionPlan.MonthlyAiReplyLimit,
                    subscription.CurrentPeriodStart,
                    subscription.CurrentPeriodEnd,
                }),
            cancellationToken);

        // A workspace with no subscription - platform staff, or a trial not yet started - buys
        // nothing, so it is entitled to nothing rather than to everything.
        if (plan is null)
        {
            return new AutoReplyAllowance("No", HasAiModule: false, [], 0, 0, null);
        }

        // Counted from the messages actually sent. A stored counter would drift the first time a send
        // failed after it was incremented, and the allowance is the customer's money.
        var periodStart = plan.CurrentPeriodStart == default
            ? new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero)
            : plan.CurrentPeriodStart;

        var used = await _queries.CountAsync(
            _messages.Query()
                .IgnoreQueryFilters()
                .Where(message =>
                    !message.IsDeleted
                    && message.TenantId == tenantId
                    && message.IsAutoReply
                    && message.OccurredAt >= periodStart),
            cancellationToken);

        return new AutoReplyAllowance(
            plan.Name,
            plan.EnabledModules.Contains(PlanModules.Ai, StringComparer.OrdinalIgnoreCase),
            plan.AutoReplyTriggers,
            plan.MonthlyAiReplyLimit,
            used,
            plan.CurrentPeriodEnd == default ? null : plan.CurrentPeriodEnd);
    }
}
