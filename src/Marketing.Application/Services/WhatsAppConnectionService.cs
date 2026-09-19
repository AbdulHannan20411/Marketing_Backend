using Marketing.Application.DTOs.WhatsApp;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Application.Interfaces;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services;

/// <summary>Connects, refreshes and disconnects a tenant's Meta WhatsApp Business Account.</summary>
public interface IWhatsAppConnectionService
{
    /// <summary>Completes Embedded Signup and stores the resulting token.</summary>
    public Task<WhatsAppConnectionResponse> ConnectAsync(
        ConnectWhatsAppRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Connects using a directly supplied token. Platform staff only.</summary>
    public Task<WhatsAppConnectionResponse> ConnectManuallyAsync(
        ManualConnectWhatsAppRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Disconnects one number and destroys its stored token.</summary>
    /// <param name="accountId">Public account id, or null for the workspace default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<WhatsAppConnectionResponse> DisconnectAsync(
        string? accountId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects a number already resolved and authorised by the caller.
    /// </summary>
    /// <param name="connection">The tracked connection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task DisconnectConnectionAsync(WhatsAppConnection connection, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes a number the workspace default, or - given null - the oldest connected number, if any.
    /// </summary>
    /// <remarks>
    /// Clears the old default and saves before setting the new one. Exactly one default per
    /// workspace is a unique index, and Postgres checks it row by row, so flipping both in one
    /// statement batch can fail on whichever update runs first.
    /// </remarks>
    /// <param name="tenantId">Workspace.</param>
    /// <param name="target">The number to make default, or null to pick one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SetDefaultAsync(long tenantId, WhatsAppConnection? target, CancellationToken cancellationToken = default);

    /// <summary>
    /// Advances every connection still part-way through onboarding.
    /// </summary>
    /// <remarks>
    /// Driven by the scheduler. Safe to call concurrently and safe to call repeatedly: a step that
    /// has already succeeded is not run again, so a second caller finds nothing left to do.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many connections were advanced.</returns>
    public Task<int> RunPendingOnboardingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retries a connection whose onboarding stopped part-way, using the credential already stored.
    /// </summary>
    /// <remarks>
    /// Exists because a failure at subscribe, register or profile is not a credential problem: the
    /// token was exchanged and stored before any of them ran. Without this the only recovery is the
    /// whole Meta popup again, and the authorisation code is single use - so the customer repeats
    /// every step to fix one Graph call that failed.
    /// <para>
    /// Steps that already succeeded are not repeated. Only the ones that failed or never ran are
    /// attempted, which matters most for registration: re-registering a number is not free.
    /// </para>
    /// </remarks>
    /// <param name="accountId">Public account id, or null for the workspace default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="NotFoundException">No connection to resume.</exception>
    /// <exception cref="BusinessRuleException">
    /// The stored credential was itself rejected, so retrying cannot help - the customer has to
    /// connect again with a new one.
    /// </exception>
    public Task<WhatsAppConnectionResponse> ResumeOnboardingAsync(
        string? accountId = null,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IWhatsAppConnectionService" />
public sealed partial class WhatsAppConnectionService : IWhatsAppConnectionService
{
    private readonly IWhatsAppConnectionRepository _connections;
    private readonly IWhatsAppGateway _gateway;
    private readonly ISecretProtector _protector;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext _tenantContext;
    private readonly IDateTimeProvider _clock;
    private readonly WhatsApp.IWhatsAppAccessService _access;
    private readonly IPlanGuard _planGuard;
    private readonly ILogger<WhatsAppConnectionService> _logger;

    /// <summary>Initialises a new instance.</summary>
    public WhatsAppConnectionService(
        IWhatsAppConnectionRepository connections,
        IWhatsAppGateway gateway,
        ISecretProtector protector,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext,
        IDateTimeProvider clock,
        WhatsApp.IWhatsAppAccessService access,
        IPlanGuard planGuard,
        ILogger<WhatsAppConnectionService> logger)
    {
        _connections = connections;
        _gateway = gateway;
        _protector = protector;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
        _clock = clock;
        _access = access;
        _planGuard = planGuard;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<WhatsAppConnectionResponse> ConnectAsync(
        ConnectWhatsAppRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Before the exchange, not after. Redeeming the code registers the number with this app at
        // Meta; refusing it afterwards over the plan or a clash leaves it half connected, and the
        // single-use code is already spent.
        await EnsureConnectableAsync(request.PhoneNumberId, request.Label, cancellationToken);

        // Redeemed server-side. The app secret needed to exchange the code must never reach the
        // browser, which is the entire reason this endpoint exists rather than the client calling
        // Meta directly.
        MetaAccessToken exchange;

        try
        {
            exchange = await _gateway.ExchangeCodeAsync(request.Code, cancellationToken);
        }
        catch (ExternalServiceException exception) when (!exception.IsTransient)
        {
            // A code is single use and short lived, so a non-transient refusal means this one
            // cannot be redeemed at all - most often a double-clicked button replaying a code the
            // first click already spent. Reported as a conflict rather than a bad gateway: nothing
            // is wrong with Meta, and 502 would tell the client to retry something that can only
            // fail again. A transient fault is rethrown untouched, because that one is worth
            // retrying.
            throw new BusinessRuleException(
                "whatsapp_code_not_redeemable",
                "That sign-up could not be completed - the authorisation has already been used or "
                + "has expired. Start the connection again.");
        }

        return await StoreAsync(
            exchange.Value,
            request.WabaId,
            request.PhoneNumberId,
            exchange.ExpiresAtUtc,
            isManual: false,
            request.Label,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<WhatsAppConnectionResponse> ConnectManuallyAsync(
        ManualConnectWhatsAppRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await EnsureConnectableAsync(request.PhoneNumberId, request.Label, cancellationToken);

        // Asked, not assumed. Both a system-user token (no expiry) and a test-number token (often
        // hours) arrive through this same box, and the earlier assumption that a pasted token was
        // permanent recorded the short-lived kind as never expiring - the precise silent failure
        // the expiry field exists to prevent. A null answer here still means "no stated expiry".
        var expiresAt = await _gateway.GetTokenExpiryAsync(request.AccessToken, cancellationToken);

        return await StoreAsync(
            request.AccessToken,
            request.WabaId,
            request.PhoneNumberId,
            expiresAt,
            isManual: true,
            request.Label,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<WhatsAppConnectionResponse> DisconnectAsync(
        string? accountId = null,
        CancellationToken cancellationToken = default)
    {
        var connection = await _access.ResolveAsync(accountId, WhatsAppAccessLevel.View, cancellationToken)
                         ?? throw new NotFoundException("No WhatsApp account is connected.");

        await DisconnectConnectionAsync(connection, cancellationToken);

        return Map(connection);
    }

    /// <inheritdoc />
    public async Task DisconnectConnectionAsync(
        WhatsAppConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var tenantId = connection.TenantId ?? _tenantContext.RequireTenantId();

        // Unsubscribed while the token still exists to do it with - but only when this was the last
        // live number on the business account, because the subscription belongs to the account and
        // a second number on it would go silent.
        if (connection is { WabaId: { Length: > 0 } wabaId, EncryptedAccessToken: { Length: > 0 } encrypted }
            && !await _connections.IsWabaInUseElsewhereAsync(wabaId, connection.Id, cancellationToken))
        {
            try
            {
                await _gateway.UnsubscribeFromWebhooksAsync(wabaId, _protector.Unprotect(encrypted), cancellationToken);
            }
            catch (Exception exception) when (exception is ExternalServiceException or BusinessRuleException)
            {
                // Best effort. Webhooks for a number with no live connection are already dropped on
                // arrival, so a failed unsubscribe costs noise, while refusing to disconnect would
                // keep a credential the customer asked us to destroy.
                LogUnsubscribeFailed(exception, connection.Id);
            }
        }

        var wasDefault = connection.IsDefault;

        connection.Status = ConnectionStatus.Disconnected;
        connection.WebhookHealthy = false;
        connection.ConnectedAt = null;
        connection.ApiStatus = "unknown";

        // The token is destroyed, not just orphaned. Leaving a live credential in a row nobody
        // reads is how a disconnected account still gets used months later. The row itself stays:
        // its conversations and employee access come back if the same number is reconnected.
        connection.EncryptedAccessToken = null;
        connection.TokenExpiresAt = null;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        if (wasDefault)
        {
            // The default moves to the oldest number still connected, or to none.
            await SetDefaultAsync(tenantId, null, cancellationToken);
        }

        LogDisconnected(tenantId);
    }

    /// <inheritdoc />
    public async Task SetDefaultAsync(
        long tenantId,
        WhatsAppConnection? target,
        CancellationToken cancellationToken = default)
    {
        var all = await _connections.FindAllForTenantAsync(tenantId, cancellationToken);

        target ??= all
            .Where(candidate => candidate.Status == ConnectionStatus.Connected)
            .OrderBy(candidate => candidate.Id)
            .FirstOrDefault();

        var changed = false;

        foreach (var other in all.Where(candidate => candidate.IsDefault && !ReferenceEquals(candidate, target)))
        {
            other.IsDefault = false;
            changed = true;
        }

        if (changed)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        if (target is { IsDefault: false })
        {
            target.IsDefault = true;

            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Refuses a connection that the plan or another workspace rules out, before anything is spent.
    /// </summary>
    private async Task EnsureConnectableAsync(string phoneNumberId, string? label, CancellationToken cancellationToken)
    {
        var tenantId = _tenantContext.RequireTenantId();

        if (label is not null)
        {
            WhatsAppAccountRules.ValidateLabel(label);
        }

        // Inbound webhooks are routed by phone number id alone, so letting two tenants hold one
        // number would deliver a customer's conversations to a stranger.
        var claimant = await _connections.FindByPhoneNumberIdAsync(phoneNumberId, cancellationToken);

        if (claimant is not null && claimant.TenantId != tenantId)
        {
            throw new BusinessRuleException(
                "whatsapp_number_in_use",
                "That phone number is connected to another workspace. Disconnect it there first, or "
                + "connect a different number.");
        }

        var existing = await _connections.FindAllForTenantAsync(tenantId, cancellationToken);

        // Re-linking a number this workspace already holds uses no new slot.
        if (existing.Any(connection => connection.PhoneNumberId == phoneNumberId))
        {
            return;
        }

        var plan = await _planGuard.CurrentPlanAsync(cancellationToken);

        // Every number not deleted counts, disconnected ones included. Otherwise disconnecting and
        // connecting another would take a workspace past its plan one number at a time.
        if (plan?.MaxWhatsAppAccounts is { } limit && existing.Count >= limit)
        {
            throw new BusinessRuleException(
                "whatsapp_account_limit_reached",
                $"Your plan includes {limit} WhatsApp {(limit == 1 ? "number" : "numbers")}, and all of them are "
                + "in use. Remove a disconnected number or upgrade your plan to connect another.");
        }
    }

    /// <summary>
    /// Verifies the number against Meta, then stores the connection with the token encrypted.
    /// </summary>
    private async Task<WhatsAppConnectionResponse> StoreAsync(
        string accessToken,
        string wabaId,
        string phoneNumberId,
        DateTimeOffset? expiresAt,
        bool isManual,
        string? label,
        CancellationToken cancellationToken)
    {
        var tenantId = _tenantContext.RequireTenantId();

        // Checked again after the exchange: another request may have taken the number or the last
        // slot while this one was talking to Meta.
        await EnsureConnectableAsync(phoneNumberId, label, cancellationToken);

        var existing = await _connections.FindAllForTenantAsync(tenantId, cancellationToken);

        // The same number again is a re-link of that account - token refreshed, history and
        // employee access intact - never a second account for one number.
        var connection = existing.FirstOrDefault(candidate => candidate.PhoneNumberId == phoneNumberId);

        if (connection is null)
        {
            connection = new WhatsAppConnection
            {
                TenantId = tenantId,
            };

            // Named now, from what Meta says the number is, so the account is recognisable in the
            // selector while onboarding runs. Best effort: the name is cosmetic and the profile step
            // reads the same fields again.
            await PrefillProfileAsync(connection, phoneNumberId, accessToken, cancellationToken);

            connection.Label = WhatsAppAccountRules.UniqueLabel(
                label?.Trim() is { Length: > 0 } chosen
                    ? chosen
                    : WhatsAppAccountRules.DefaultLabel(connection.VerifiedName, connection.DisplayPhoneNumber),
                existing.Select(other => other.Label),
                allowSuffix: label is null);

            _connections.Add(connection);
        }
        else if (label?.Trim() is { Length: > 0 } relabel)
        {
            connection.Label = WhatsAppAccountRules.UniqueLabel(
                relabel,
                existing.Where(other => other.Id != connection.Id).Select(other => other.Label),
                allowSuffix: false);
        }

        connection.WabaId = wabaId;
        connection.PhoneNumberId = phoneNumberId;
        connection.EncryptedAccessToken = _protector.Protect(accessToken);
        connection.TokenExpiresAt = expiresAt;
        connection.Status = ConnectionStatus.Pending;

        // Seeded up front so the client has the whole list to render immediately, including steps
        // that have not started. A panel that grows a row at a time reads as instability; one that
        // shows every step and lights them up reads as progress.
        connection.OnboardingSteps =
        [
            new WhatsAppOnboardingStep
            {
                // Already done by the time anything is stored: the code was exchanged, or the
                // pasted token inspected, before this method was reached.
                Step = OnboardingStep.Token,
                Status = OnboardingStepStatus.Succeeded,
                CompletedAt = _clock.UtcNow,
            },
            new WhatsAppOnboardingStep { Step = OnboardingStep.Subscribe },
            new WhatsAppOnboardingStep { Step = OnboardingStep.Register },
            new WhatsAppOnboardingStep { Step = OnboardingStep.Profile },
        ];

        connection.LastError = null;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // The first number a workspace connects is its default, as is any number connected while
        // the workspace has none.
        if (!existing.Any(other => other.IsDefault && other.Id != connection.Id))
        {
            await SetDefaultAsync(tenantId, connection, cancellationToken);
        }

        LogConnected(tenantId, phoneNumberId, isManual);

        // Returns here rather than running the remaining steps inline. Subscribing, registering and
        // reading the profile are three round trips to Meta that regularly take seconds and can
        // each fail for their own reason; holding the request open gave the caller one opaque
        // answer at the end, and a browser that gave up first got no answer at all. The poller
        // picks the connection up within seconds and the client watches the steps.
        return Map(connection);
    }

    /// <inheritdoc />
    public async Task<WhatsAppConnectionResponse> ResumeOnboardingAsync(
        string? accountId = null,
        CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantContext.RequireTenantId();

        var connection = await _access.ResolveAsync(accountId, WhatsAppAccessLevel.View, cancellationToken)
                         ?? throw new NotFoundException("No WhatsApp account is connected.");

        if (connection.EncryptedAccessToken is not { Length: > 0 })
        {
            // Disconnected, or never got as far as storing a credential. There is nothing to resume
            // with, and saying so is better than starting a run that fails at the first call.
            throw new BusinessRuleException(
                "whatsapp_nothing_to_resume",
                "There is no stored credential to retry with. Connect the account again.");
        }

        // A rejected credential is the one failure retrying cannot fix: every attempt will use the
        // same token and be refused the same way. Refused here so the client can send the customer
        // back to signup instead of offering a retry that is guaranteed to fail.
        var rejected = connection.OnboardingSteps.FirstOrDefault(step =>
            step.Status == OnboardingStepStatus.Failed && step.Code == "token_rejected");

        if (rejected is not null)
        {
            throw new BusinessRuleException(
                "whatsapp_token_rejected",
                "Meta refused the stored credential, so retrying will not help. Connect the account again.");
        }

        // Failed steps are returned to pending; succeeded and skipped ones are left alone, so the
        // run picks up where it stopped rather than starting over.
        foreach (var step in connection.OnboardingSteps.Where(step =>
                     step.Status is OnboardingStepStatus.Failed or OnboardingStepStatus.Running))
        {
            step.Status = OnboardingStepStatus.Pending;
            step.Code = null;
            step.Message = null;
            step.CompletedAt = null;
        }

        // Back to pending, which is what the poller looks for. The work itself is not done inline:
        // it is the same three Graph calls that were moved out of the request in the first place.
        connection.Status = ConnectionStatus.Pending;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        LogOnboardingResumed(tenantId);

        return Map(connection);
    }

    /// <inheritdoc />
    public async Task<int> RunPendingOnboardingAsync(CancellationToken cancellationToken = default)
    {
        var tenants = await _connections.FindTenantsAwaitingOnboardingAsync(
            TenantsPerPoll, cancellationToken);

        var advanced = 0;

        foreach (var tenantId in tenants)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Entered per tenant, so every query below - and the Graph handler's own token lookup -
            // is confined to this tenant and no other.
            using (_tenantContext.BeginScope(tenantId))
            {
                try
                {
                    await ContinueOnboardingAsync(tenantId, cancellationToken);
                    advanced++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One tenant's failed onboarding must not stop the rest. The connection keeps
                    // the step state it reached and is picked up again on the next poll.
                    LogOnboardingFailed(exception, tenantId);
                }
            }
        }

        return advanced;
    }

    /// <summary>Runs whatever steps remain for each of one tenant's pending numbers.</summary>
    private async Task ContinueOnboardingAsync(long tenantId, CancellationToken cancellationToken)
    {
        var pending = (await _connections.FindAllForTenantAsync(tenantId, cancellationToken))
            .Where(connection => connection.Status == ConnectionStatus.Pending)
            .ToList();

        foreach (var connection in pending)
        {
            await ContinueOnboardingAsync(connection, cancellationToken);
        }
    }

    /// <summary>Runs whatever steps remain for one pending number.</summary>
    private async Task ContinueOnboardingAsync(WhatsAppConnection connection, CancellationToken cancellationToken)
    {
        if (connection is not { Status: ConnectionStatus.Pending, PhoneNumberId: { Length: > 0 } phoneNumberId }
            || connection.WabaId is not { Length: > 0 } wabaId
            || connection.EncryptedAccessToken is not { Length: > 0 } encrypted)
        {
            return;
        }

        // Decrypted once and passed to each call. The handler that normally supplies it resolves
        // the tenant in its own scope, which a poller's scope does not reach - so it would find no
        // tenant, send no credential, and Meta would refuse every call as unauthenticated.
        var accessToken = _protector.Unprotect(encrypted);

        if (!await TryStepAsync(
                connection,
                OnboardingStep.Subscribe,
                () => _gateway.SubscribeToWebhooksAsync(wabaId, accessToken, cancellationToken),
                cancellationToken))
        {
            return;
        }

        // Registration is the one step whose failure is not a failure. Every Meta test number, and
        // every number onboarded through Embedded Signup, is already registered and rejects a
        // second attempt because the PIN it holds is not the one being offered. Recorded as skipped
        // so the panel says so plainly instead of showing a red step on a working connection.
        connection.RegistrationPin ??= NewRegistrationPin();

        await TryStepAsync(
            connection,
            OnboardingStep.Register,
            () => _gateway.RegisterPhoneNumberAsync(
                phoneNumberId, connection.RegistrationPin!, accessToken, cancellationToken),
            cancellationToken,
            failureIsSkip: true);

        if (!await TryStepAsync(
                connection,
                OnboardingStep.Profile,
                async () =>
                {
                    var number = await _gateway.GetPhoneNumberAsync(
                        phoneNumberId, accessToken, cancellationToken);

                    connection.DisplayPhoneNumber = number.DisplayPhoneNumber;
                    connection.VerifiedName = number.VerifiedName ?? string.Empty;
                    connection.QualityRating = ParseQuality(number.QualityRating);
                    connection.PhoneNumberStatus = number.Status ?? connection.PhoneNumberStatus;

                    // Absent means "leave what we had". Meta omits the tier on numbers it has not
                    // rated, and defaulting to the lowest would tell the customer their throughput
                    // had dropped when nothing had changed.
                    connection.MessagingTier = ParseTier(number.MessagingTier) ?? connection.MessagingTier;

                    var account = await _gateway.GetBusinessAccountAsync(
                        wabaId, accessToken, cancellationToken);

                    connection.TemplateNamespaceAlias =
                        account.TemplateNamespace ?? connection.TemplateNamespaceAlias;
                    connection.AccountStatus = account.ReviewStatus ?? connection.AccountStatus;
                },
                cancellationToken))
        {
            return;
        }

        connection.Status = ConnectionStatus.Connected;
        connection.ConnectedAt = _clock.UtcNow;
        connection.ApiStatus = "ok";
        connection.LastError = null;

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Reads the number's name before it is stored, when Meta will say.</summary>
    private async Task PrefillProfileAsync(
        WhatsAppConnection connection,
        string phoneNumberId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var number = await _gateway.GetPhoneNumberAsync(phoneNumberId, accessToken, cancellationToken);

            connection.DisplayPhoneNumber = number.DisplayPhoneNumber;
            connection.VerifiedName = number.VerifiedName ?? string.Empty;
        }
        catch (Exception exception) when (exception is ExternalServiceException or BusinessRuleException)
        {
            // The profile step reads it again; a label from the fallback is all this costs.
            LogPrefillFailed(exception, phoneNumberId);
        }
    }

    /// <summary>
    /// Runs one step, recording what happened to it either way.
    /// </summary>
    /// <returns><see langword="true"/> when onboarding may continue.</returns>
    private async Task<bool> TryStepAsync(
        WhatsAppConnection connection,
        OnboardingStep step,
        Func<Task> work,
        CancellationToken cancellationToken,
        bool failureIsSkip = false)
    {
        var record = connection.OnboardingSteps.FirstOrDefault(entry => entry.Step == step);

        if (record is null)
        {
            // A connection stored before this feature existed has no step list. Adding the row
            // rather than refusing lets those connections finish onboarding normally.
            record = new WhatsAppOnboardingStep { Step = step };
            connection.OnboardingSteps.Add(record);
        }

        if (record.Status is OnboardingStepStatus.Succeeded or OnboardingStepStatus.Skipped)
        {
            // Already settled on an earlier poll. Re-running subscribe or register is not free and,
            // for register, actively harmful.
            return true;
        }

        record.Status = OnboardingStepStatus.Running;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        try
        {
            await work();

            record.Status = OnboardingStepStatus.Succeeded;
            record.Code = null;
            record.Message = null;
        }
        catch (Exception exception) when (exception is BusinessRuleException or ExternalServiceException)
        {
            record.Status = failureIsSkip ? OnboardingStepStatus.Skipped : OnboardingStepStatus.Failed;
            record.Code = failureIsSkip ? null : CodeFor(step, exception);
            record.Message = Truncate(exception.Message);

            if (!failureIsSkip)
            {
                connection.Status = ConnectionStatus.Error;
                connection.ApiStatus = "down";
                connection.LastError = WhatsAppAccountRules.PlainError(step);
            }
        }

        record.CompletedAt = _clock.UtcNow;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return record.Status is not OnboardingStepStatus.Failed;
    }

    /// <summary>Maps a step failure to the stable code the client turns into a remedy.</summary>
    /// <remarks>
    /// Deliberately coarse. A code per step, plus the one distinction that changes the advice -
    /// whether the credential itself is the problem - is enough for the client to say something
    /// useful, and a finer taxonomy would break the moment Meta reworded an error.
    /// </remarks>
    private static string CodeFor(OnboardingStep step, Exception exception)
    {
        // The credential, not the step. Any step can report it and the remedy is always the same:
        // reconnect. Checked first because it would otherwise be blamed on whichever call happened
        // to be running when the token lapsed.
        if (exception.Message.Contains("401", StringComparison.Ordinal)
            || exception.Message.Contains("Code 190", StringComparison.Ordinal))
        {
            return "token_rejected";
        }

        return step switch
        {
            OnboardingStep.Subscribe => "subscribe_refused",
            OnboardingStep.Register => "register_refused",
            OnboardingStep.Profile => "profile_unreadable",
            _ => "onboarding_failed",
        };
    }

    /// <summary>Keeps an operator message inside the column that stores it.</summary>
    private static string Truncate(string message) =>
        message.Length <= 500 ? message : message[..500];

    /// <summary>
    /// Reads Meta's tier string, or null when it says nothing.
    /// </summary>
    /// <remarks>
    /// Meta reports <c>TIER_1K</c> and similar. Null is returned rather than the lowest tier so the
    /// caller can distinguish "Meta did not say" from "Meta said 250".
    /// </remarks>
    private static MessagingTier? ParseTier(string? tier) => tier?.Trim().ToUpperInvariant() switch
    {
        "TIER_50" or "TIER_250" => MessagingTier.Tier250,
        "TIER_1K" => MessagingTier.Tier1K,
        "TIER_10K" => MessagingTier.Tier10K,
        "TIER_100K" => MessagingTier.Tier100K,
        "TIER_UNLIMITED" or "UNLIMITED" => MessagingTier.Unlimited,
        _ => null,
    };

    /// <summary>
    /// Generates the six-digit PIN Meta requires to register a number.
    /// </summary>
    /// <remarks>
    /// Cryptographically random rather than sequential or derived: it is two-factor material for
    /// the customer's number, and a guessable one would let somebody else re-register it.
    /// </remarks>
    private static string NewRegistrationPin() =>
        System.Security.Cryptography.RandomNumberGenerator.GetInt32(100_000, 1_000_000)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static QualityRating ParseQuality(string? rating) =>
        Enum.TryParse<QualityRating>(rating, ignoreCase: true, out var parsed) ? parsed : QualityRating.Green;

    /// <summary>
    /// Tenants advanced per poll.
    /// <para>
    /// Bounded so one poll cannot hold the scheduler while it walks every stalled connection on the
    /// platform. Onboarding is rare and short-lived, so a small number clears the queue quickly.
    /// </para>
    /// </summary>
    private const int TenantsPerPoll = 20;

    private static WhatsAppConnectionResponse Map(WhatsAppConnection connection) =>
        connection.ToResponse();

    [LoggerMessage(
        EventId = 2601,
        Level = LogLevel.Information,
        Message = "Tenant {TenantId} connected WhatsApp number {PhoneNumberId}. Manual: {IsManual}.")]
    private partial void LogConnected(long tenantId, string phoneNumberId, bool isManual);

    [LoggerMessage(
        EventId = 2602,
        Level = LogLevel.Warning,
        Message = "Could not verify WhatsApp number {PhoneNumberId} for tenant {TenantId}.")]
    private partial void LogVerificationFailed(Exception exception, long tenantId, string phoneNumberId);

    [LoggerMessage(
        EventId = 2604,
        Level = LogLevel.Information,
        Message = "Registration skipped for number {PhoneNumberId}: it is already registered. "
                  + "The profile read decides whether the number is usable.")]
    private partial void LogRegistrationSkipped(Exception exception, string phoneNumberId);

    [LoggerMessage(
        EventId = 2603,
        Level = LogLevel.Warning,
        Message = "Tenant {TenantId} disconnected WhatsApp; the stored token was destroyed.")]
    private partial void LogDisconnected(long tenantId);

    [LoggerMessage(
        EventId = 2604,
        Level = LogLevel.Error,
        Message = "Onboarding could not be advanced for tenant {TenantId}.")]
    private partial void LogOnboardingFailed(Exception exception, long tenantId);

    [LoggerMessage(
        EventId = 2606,
        Level = LogLevel.Warning,
        Message = "Could not unsubscribe webhooks while disconnecting WhatsApp connection {ConnectionId}.")]
    private partial void LogUnsubscribeFailed(Exception exception, long connectionId);

    [LoggerMessage(
        EventId = 2607,
        Level = LogLevel.Information,
        Message = "Could not read number {PhoneNumberId} before storing it; the profile step will.")]
    private partial void LogPrefillFailed(Exception exception, string phoneNumberId);

    [LoggerMessage(
        EventId = 2605,
        Level = LogLevel.Information,
        Message = "Tenant {TenantId} resumed WhatsApp onboarding from a failed step.")]
    private partial void LogOnboardingResumed(long tenantId);
}
