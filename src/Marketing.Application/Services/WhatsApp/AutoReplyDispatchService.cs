using System.Globalization;
using System.Text;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.WhatsApp;

/// <summary>Sends the automatic replies that have come due.</summary>
public interface IAutoReplyDispatchService
{
    /// <summary>Answers every customer who has waited long enough, across all workspaces.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many replies were sent.</returns>
    public Task<int> DispatchDueAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAutoReplyDispatchService" />
/// <remarks>
/// Polling rather than a queue. A reply's due time moves every time the customer writes again, and an
/// agent answering by hand cancels it outright - both are answered by asking "is the newest message
/// still unanswered?" at the moment of sending, where a queued job would have to be found and
/// withdrawn. The check is one indexed query, and it is always current.
/// <para>
/// Only conversations with an open 24-hour window are considered, because a free-form reply is the
/// only thing being sent: outside it Meta accepts nothing but an approved template, and a template is
/// a campaign decision rather than an automatic one.
/// </para>
/// </remarks>
public sealed partial class AutoReplyDispatchService : IAutoReplyDispatchService
{
    /// <summary>Conversations examined in one pass, newest first.</summary>
    private const int BatchSize = 100;

    /// <summary>Messages of history handed to the model.</summary>
    private const int HistoryDepth = 8;

    /// <summary>Longest reply worth sending to a chat window.</summary>
    private const int ReplyMaxLength = 600;

    private readonly IRepository<Conversation> _conversations;
    private readonly IRepository<ConversationMessage> _messages;
    private readonly IRepository<AutoReplySettings> _settings;
    private readonly IAutoReplyKnowledgeRepository _knowledge;
    private readonly IRepository<AutoReplyAttempt> _attempts;
    private readonly IRepository<Notification> _notifications;
    private readonly IWhatsAppConnectionRepository _connections;
    private readonly IUserRepository _users;
    private readonly IAutoReplyAllowance _allowance;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAiService _ai;
    private readonly IWhatsAppGateway _gateway;
    private readonly ISecretProtector _protector;
    private readonly ITenantContext _tenantContext;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<AutoReplyDispatchService> _logger;

    /// <summary>Initialises a new instance.</summary>
    public AutoReplyDispatchService(
        IRepository<Conversation> conversations,
        IRepository<ConversationMessage> messages,
        IRepository<AutoReplySettings> settings,
        IAutoReplyKnowledgeRepository knowledge,
        IRepository<AutoReplyAttempt> attempts,
        IRepository<Notification> notifications,
        IWhatsAppConnectionRepository connections,
        IUserRepository users,
        IAutoReplyAllowance allowance,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        IAiService ai,
        IWhatsAppGateway gateway,
        ISecretProtector protector,
        ITenantContext tenantContext,
        IDateTimeProvider clock,
        ILogger<AutoReplyDispatchService> logger)
    {
        _conversations = conversations;
        _messages = messages;
        _settings = settings;
        _knowledge = knowledge;
        _attempts = attempts;
        _notifications = notifications;
        _connections = connections;
        _users = users;
        _allowance = allowance;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _ai = ai;
        _gateway = gateway;
        _protector = protector;
        _tenantContext = tenantContext;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> DispatchDueAsync(CancellationToken cancellationToken = default)
    {
        if (!_ai.IsConfigured)
        {
            // No model key on this deployment. Nothing to do, and nothing worth logging every ten
            // seconds about it.
            return 0;
        }

        var now = _clock.UtcNow;
        var candidates = await FindWaitingAsync(now, cancellationToken);

        if (candidates.Count == 0)
        {
            return 0;
        }

        var sent = 0;

        foreach (var group in candidates.GroupBy(candidate => candidate.TenantId))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                sent += await DispatchTenantAsync(group.Key, [.. group], now, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One workspace's bad token or refused model call must not stop every other
                // workspace's replies for the rest of the run.
                LogTenantFailed(exception, group.Key);
            }
        }

        return sent;
    }

    /// <summary>Answers the customers of one workspace, if its plan and rules allow it.</summary>
    private async Task<int> DispatchTenantAsync(
        long tenantId,
        IReadOnlyList<WaitingConversation> waiting,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var scope = _tenantContext.BeginScope(tenantId);

        var settings = await _queries.FirstOrDefaultAsync(
            _settings.Query(asNoTracking: false),
            cancellationToken);

        if (settings is not { Enabled: true })
        {
            return 0;
        }

        var allowance = await _allowance.ForTenantAsync(tenantId, cancellationToken);

        if (!allowance.HasAiModule)
        {
            return 0;
        }

        // Every number the workspace can send from. A reply goes out from the number the customer
        // wrote to - from any other it would arrive as a message from a stranger.
        var senders = (await _connections.FindAllForTenantAsync(tenantId, cancellationToken))
            .Where(connection => connection is
            {
                Status: not ConnectionStatus.Disconnected,
                PhoneNumberId: { Length: > 0 },
                EncryptedAccessToken: { Length: > 0 },
            })
            .ToList();

        if (senders.Count == 0)
        {
            return 0;
        }

        var workspaceDefault = senders.FirstOrDefault(connection => connection.IsDefault) ?? senders[0];

        var due = waiting
            .Select(candidate => (Candidate: candidate, Trigger: TriggerFor(candidate, settings, allowance, now)))
            .Where(pair => pair.Trigger is not null)
            .ToList();

        if (due.Count == 0)
        {
            return 0;
        }

        if (!allowance.HasHeadroom)
        {
            // Stopped, and said so once. An admin who is not told simply sees the feature go quiet and
            // assumes it broke.
            await AnnounceExhaustedAllowanceAsync(tenantId, settings, allowance, cancellationToken);

            return 0;
        }

        // Read once per workspace per run. When there are entries they replace the free-text
        // instructions entirely; when there are none, replies behave exactly as they always have.
        var knowledge = await _queries.ToListAsync(
            _knowledge.Query().OrderBy(entry => entry.SortOrder).ThenBy(entry => entry.Id),
            cancellationToken);

        var tokens = new Dictionary<long, string>();
        var remaining = allowance.Remaining;
        var sent = 0;

        foreach (var (candidate, trigger) in due)
        {
            if (remaining is <= 0)
            {
                await AnnounceExhaustedAllowanceAsync(tenantId, settings, allowance, cancellationToken);

                break;
            }

            // A thread written before there were several numbers has none recorded and belongs to the
            // default; one whose number has since been disconnected is skipped rather than answered
            // from somewhere else.
            var sender = candidate.ConnectionId is { } connectionId
                ? senders.FirstOrDefault(connection => connection.Id == connectionId)
                : workspaceDefault;

            if (sender is null)
            {
                continue;
            }

            if (!tokens.TryGetValue(sender.Id, out var accessToken))
            {
                tokens[sender.Id] = accessToken = _protector.Unprotect(sender.EncryptedAccessToken!);
            }

            if (await SendAsync(
                    candidate, trigger!, settings, knowledge, sender.PhoneNumberId!, accessToken, now, cancellationToken))
            {
                sent++;
                remaining = remaining is { } left ? left - 1 : null;
            }
        }

        return sent;
    }

    /// <summary>Writes one reply, having asked the model for it.</summary>
    private async Task<bool> SendAsync(
        WaitingConversation candidate,
        string trigger,
        AutoReplySettings settings,
        IReadOnlyList<AutoReplyKnowledgeEntry> knowledge,
        string phoneNumberId,
        string accessToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var history = await _queries.ToListAsync(
            _messages.Query()
                .Where(message => message.ConversationId == candidate.ConversationId)
                .OrderByDescending(message => message.OccurredAt)
                .ThenByDescending(message => message.Id)
                .Take(HistoryDepth)
                .Select(message => new { message.Direction, message.Body, message.Kind }),
            cancellationToken);

        var transcript = history
            .Reverse()
            .Where(message => message.Body.Length > 0)
            .Select(message => (message.Direction == MessageDirection.Inbound ? "Customer: " : "Business: ") + message.Body);

        if (knowledge.Count > 0)
        {
            return await AnswerFromKnowledgeAsync(
                candidate, trigger, settings, knowledge, transcript, phoneNumberId, accessToken, now, cancellationToken);
        }

        string answer;

        try
        {
            answer = await _ai.GenerateAsync(
                BuildPrompt(settings, candidate.ContactName, trigger, transcript),
                cancellationToken);
        }
        catch (Exception exception) when (exception is AppException)
        {
            // A refused or blocked generation costs nothing and is not recorded against the allowance:
            // no message was sent, so the customer is exactly where they were.
            LogGenerationFailed(exception, candidate.ConversationId);

            return false;
        }

        answer = Trim(answer);

        if (answer.Length == 0)
        {
            return false;
        }

        return await DeliverAsync(candidate, trigger, answer, phoneNumberId, accessToken, now, cancellationToken);
    }

    /// <summary>
    /// Answers from the knowledge file only - or, when it has no answer, falls back as the workspace chose.
    /// </summary>
    private async Task<bool> AnswerFromKnowledgeAsync(
        WaitingConversation candidate,
        string trigger,
        AutoReplySettings settings,
        IReadOnlyList<AutoReplyKnowledgeEntry> knowledge,
        IEnumerable<string> transcript,
        string phoneNumberId,
        string accessToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Each customer message is put to the model once. Without this an unanswerable question would
        // be asked again on every run until someone replied, at the model's price each time.
        var attempted = await _queries.CountAsync(
            _attempts.Query().Where(attempt =>
                attempt.ConversationId == candidate.ConversationId
                && attempt.InboundMessageAt >= candidate.LastInboundAt),
            cancellationToken);

        if (attempted > 0)
        {
            return false;
        }

        var prompt = AutoReplyKnowledgePrompt.Build(
            knowledge, candidate.LastInboundBody, trigger, candidate.ContactName, transcript);

        string raw;

        try
        {
            raw = await _ai.GenerateAsync(prompt.Text, cancellationToken);
        }
        catch (Exception exception) when (exception is AppException)
        {
            // Not recorded: a failed call is not an answer, and the next run may succeed.
            LogGenerationFailed(exception, candidate.ConversationId);

            return false;
        }

        var (answer, outcome) = Judge(raw, prompt, knowledge, trigger);

        if (outcome == OutcomeAnswered)
        {
            var sent = await DeliverAsync(candidate, trigger, answer, phoneNumberId, accessToken, now, cancellationToken);

            await RecordAttemptAsync(candidate, trigger, OutcomeAnswered, fallbackSent: false, now, cancellationToken);

            return sent;
        }

        // Not covered, or the reply failed the checks. A handoff sends the holding message at most once
        // per conversation a day: three unanswerable questions should not get three identical replies.
        var holdingMessage = settings.KnowledgeFallback == AutoReplyKnowledgeRules.Handoff
                             && settings.KnowledgeFallbackMessage.Length > 0
                             && await _queries.CountAsync(
                                 _attempts.Query().Where(attempt =>
                                     attempt.ConversationId == candidate.ConversationId
                                     && attempt.FallbackSent
                                     && attempt.AttemptedAt > now.AddHours(-24)),
                                 cancellationToken) == 0;

        var handedOff = holdingMessage
                        && await DeliverAsync(
                            candidate, trigger, settings.KnowledgeFallbackMessage, phoneNumberId, accessToken, now, cancellationToken);

        await RecordAttemptAsync(candidate, trigger, outcome, handedOff, now, cancellationToken);

        LogNotCovered(candidate.ConversationId, outcome, handedOff);

        return handedOff;
    }

    /// <summary>
    /// Decides whether a model reply may be sent: not "unknown", and quoting no price the file does
    /// not contain.
    /// </summary>
    private static (string Answer, string Outcome) Judge(
        string raw,
        AutoReplyKnowledgePrompt.Prompt prompt,
        IReadOnlyList<AutoReplyKnowledgeEntry> knowledge,
        string trigger)
    {
        var answer = AutoReplyKnowledgePrompt.IsUnknown(raw) ? string.Empty : AutoReplyKnowledgePrompt.Tidy(raw);

        // A greeting never needs the file to answer anything, so it is never "unknown": the business
        // name makes one when the model declines.
        if (answer.Length == 0 && trigger == AutoReplyTriggers.Greeting)
        {
            answer = AutoReplyKnowledgePrompt.Greeting(knowledge) ?? string.Empty;
        }

        if (answer.Length == 0)
        {
            return (string.Empty, OutcomeUnknown);
        }

        // The complaint this feature exists to prevent: a price the business never quoted.
        return AutoReplyKnowledgePrompt.HasInventedPrice(answer, prompt.Included)
            ? (string.Empty, OutcomeRejected)
            : (answer, OutcomeAnswered);
    }

    private async Task RecordAttemptAsync(
        WaitingConversation candidate,
        string trigger,
        string outcome,
        bool fallbackSent,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var question = candidate.LastInboundBody.Trim();

        _attempts.Add(new AutoReplyAttempt
        {
            TenantId = candidate.TenantId,
            ConversationId = candidate.ConversationId,
            InboundMessageAt = candidate.LastInboundAt,
            Trigger = trigger,
            Outcome = outcome,
            FallbackSent = fallbackSent,
            Question = question.Length <= 500 ? question : question[..500],
            AttemptedAt = now,
        });

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Sends one automatic message, unless someone answered while it was being written.</summary>
    private async Task<bool> DeliverAsync(
        WaitingConversation candidate,
        string trigger,
        string answer,
        string phoneNumberId,
        string accessToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var conversation = await _queries.FirstOrDefaultAsync(
            _conversations.Query(asNoTracking: false)
                .Where(existing => existing.Id == candidate.ConversationId),
            cancellationToken);

        if (conversation is null)
        {
            return false;
        }

        // Re-checked at the moment of sending, not when the batch was chosen. An agent may have
        // answered while the model was thinking, and two answers to one question is worse than a slow
        // one. The same check makes a second dispatcher harmless.
        var answered = await _queries.CountAsync(
            _messages.Query()
                .Where(message =>
                    message.ConversationId == candidate.ConversationId
                    && message.OccurredAt > candidate.LastInboundAt),
            cancellationToken);

        if (answered > 0)
        {
            return false;
        }

        var message = new ConversationMessage
        {
            TenantId = conversation.TenantId,
            ConversationId = conversation.Id,
            Direction = MessageDirection.Outbound,
            Kind = ConversationMessageKind.Text,
            Body = answer,
            Status = InboxMessageStatus.Queued,
            IsAutoReply = true,
            OccurredAt = now,
        };

        _messages.Add(message);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        try
        {
            message.MetaMessageId = await _gateway.SendTextAsync(
                phoneNumberId,
                conversation.WaId,
                answer,
                accessToken,
                cancellationToken);

            message.Status = InboxMessageStatus.Sent;
        }
        catch (ExternalServiceException exception)
        {
            message.Status = InboxMessageStatus.Failed;
            message.FailureReason = MetaSendErrors.Describe(exception.ProviderErrorCode)?.Reason ?? exception.Message;
        }

        conversation.LastMessagePreview = answer.Length <= 300 ? answer : answer[..300];
        conversation.LastMessageAt = now;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        LogReplySent(conversation.Id, trigger, message.Status);

        return message.Status == InboxMessageStatus.Sent;
    }

    /// <summary>
    /// Which rule, if any, says this customer should be answered now.
    /// </summary>
    /// <remarks>
    /// Order matters: a greeting is answered on the greeting's short pause, not left to the
    /// unanswered rule's hours. The most specific trigger that is both switched on and sold wins.
    /// </remarks>
    private static string? TriggerFor(
        WaitingConversation candidate,
        AutoReplySettings settings,
        AutoReplyAllowance allowance,
        DateTimeOffset now)
    {
        // A customer who has already been answered automatically several times today is talking to a
        // machine; the rest of the conversation belongs to a person.
        if (candidate.AutoRepliesToday >= settings.MaxPerConversationPerDay)
        {
            return null;
        }

        // Nothing useful can be said about a location or a sticker, and answering one with a greeting
        // reads as a non sequitur.
        if (candidate.LastInboundKind is ConversationMessageKind.System)
        {
            return null;
        }

        var waited = now - candidate.LastInboundAt;

        if (settings.GreetingEnabled
            && allowance.AllowsTrigger(AutoReplyTriggers.Greeting)
            && AutoReplyTriggers.IsGreeting(candidate.LastInboundBody)
            && waited >= TimeSpan.FromSeconds(settings.DelaySeconds))
        {
            return AutoReplyTriggers.Greeting;
        }

        if (settings.FirstMessageEnabled
            && allowance.AllowsTrigger(AutoReplyTriggers.FirstMessage)
            && candidate.InboundCount == 1
            && candidate.OutboundCount == 0
            && waited >= TimeSpan.FromSeconds(settings.DelaySeconds))
        {
            return AutoReplyTriggers.FirstMessage;
        }

        if (settings.UnansweredEnabled
            && allowance.AllowsTrigger(AutoReplyTriggers.Unanswered)
            && waited >= TimeSpan.FromMinutes(settings.UnansweredAfterMinutes))
        {
            return AutoReplyTriggers.Unanswered;
        }

        return null;
    }

    /// <summary>Conversations whose newest message is the customer's, across every workspace.</summary>
    private async Task<IReadOnlyList<WaitingConversation>> FindWaitingAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);

        return await _queries.ToListAsync(
            _messages.Query()
                // No principal on a scheduled run, so each row's own tenant is carried through and
                // entered explicitly before anything is written.
                .IgnoreQueryFilters()
                .Where(message =>
                    !message.IsDeleted
                    && message.TenantId != null
                    && message.Direction == MessageDirection.Inbound

                    // The window is 24 hours, so nothing older can still be answerable.
                    && message.OccurredAt > now.AddHours(-24)
                    && message.Conversation.WindowExpiresAt > now

                    // Newest in its thread: anything after it means somebody already answered.
                    && !message.Conversation.Messages.Any(later =>
                        !later.IsDeleted && later.OccurredAt > message.OccurredAt))
                .OrderByDescending(message => message.OccurredAt)
                .Take(BatchSize)
                .Select(message => new WaitingConversation(
                    message.ConversationId,
                    message.TenantId!.Value,
                    message.Conversation.ContactName,
                    message.Body,
                    message.Kind,
                    message.OccurredAt,
                    message.Conversation.Messages.Count(other =>
                        !other.IsDeleted && other.Direction == MessageDirection.Inbound),
                    message.Conversation.Messages.Count(other =>
                        !other.IsDeleted && other.Direction == MessageDirection.Outbound),
                    message.Conversation.Messages.Count(other =>
                        !other.IsDeleted && other.IsAutoReply && other.OccurredAt >= dayStart),
                    message.Conversation.WhatsAppConnectionId)),
            cancellationToken);
    }

    /// <summary>Tells the workspace's administrators the allowance is spent, once per period.</summary>
    private async Task AnnounceExhaustedAllowanceAsync(
        long tenantId,
        AutoReplySettings settings,
        AutoReplyAllowance allowance,
        CancellationToken cancellationToken)
    {
        if (settings.QuotaNoticeSentForPeriodEnd == allowance.PeriodEndsAt)
        {
            return;
        }

        var administrators = await _users.GetTenantAdministratorsAsync(tenantId, cancellationToken);
        var now = _clock.UtcNow;

        foreach (var administrator in administrators)
        {
            _notifications.Add(new Notification
            {
                TenantId = tenantId,
                UserId = administrator.Id,
                Kind = NotificationKind.AiRepliesExhausted,
                Title = "Automatic replies have paused",
                Body = $"The {allowance.PlanName} plan includes "
                       + $"{allowance.MonthlyLimit?.ToString("N0", CultureInfo.InvariantCulture) ?? "no"} automatic "
                       + "replies a month, and they have all been used. Customers now wait for a person "
                       + "until the allowance resets.",
                Priority = NotificationPriority.Warning,
                Icon = "sparkles",
                ActionLabel = "See plans",
                ActionRoute = "/subscription",
                OccurredOn = now,
            });
        }

        // Recorded against the period, so the notice returns when the next one runs out rather than
        // never being sent again.
        settings.QuotaNoticeSentForPeriodEnd = allowance.PeriodEndsAt;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        LogAllowanceExhausted(tenantId, allowance.MonthlyLimit ?? 0);
    }

    /// <summary>What the model is asked, and what it is told not to do.</summary>
    private static string BuildPrompt(
        AutoReplySettings settings,
        string contactName,
        string trigger,
        IEnumerable<string> transcript)
    {
        var prompt = new StringBuilder();

        prompt.AppendLine(
            "You are answering a WhatsApp message on behalf of a business. Write the reply itself and "
            + "nothing else: no preamble, no quotation marks, no subject line.");
        prompt.AppendLine();
        prompt.AppendLine("Rules:");
        prompt.AppendLine("- Keep it under 60 words, in the language the customer wrote in.");
        prompt.AppendLine("- Be warm and plain. No emoji unless the customer used one.");
        prompt.AppendLine(
            "- Never invent prices, delivery dates, stock or order details. If the answer needs any of "
            + "those, say a colleague will confirm shortly.");
        prompt.AppendLine("- Never claim to be a human, and never say you are an AI unless asked outright.");

        prompt.AppendLine(trigger switch
        {
            AutoReplyTriggers.Greeting => "- The customer has only said hello. Greet them and ask how you can help.",
            AutoReplyTriggers.FirstMessage =>
                "- This is their first message. Acknowledge it and say someone will follow up if it needs a person.",
            _ => "- Nobody has replied for a while. Acknowledge the wait and answer what can safely be answered.",
        });

        if (settings.Instructions is { Length: > 0 } instructions)
        {
            prompt.AppendLine();
            prompt.AppendLine("About this business, in its own words:");
            prompt.AppendLine(instructions);
        }

        prompt.AppendLine();
        prompt.AppendLine(CultureInfo.InvariantCulture, $"Customer's name, if useful: {contactName}");
        prompt.AppendLine();
        prompt.AppendLine("Conversation so far, oldest first:");

        foreach (var line in transcript)
        {
            prompt.AppendLine(line);
        }

        prompt.AppendLine();
        prompt.Append("Reply:");

        return prompt.ToString();
    }

    /// <summary>The reply was sent.</summary>
    private const string OutcomeAnswered = "answered";

    /// <summary>The knowledge file did not cover the question.</summary>
    private const string OutcomeUnknown = "unknown";

    /// <summary>The reply failed the output checks, such as an invented price.</summary>
    private const string OutcomeRejected = "rejected";

    /// <summary>Strips the things a model adds that a chat window should not show.</summary>
    private static string Trim(string answer)
    {
        var cleaned = answer.Trim().Trim('"');

        return cleaned.Length <= ReplyMaxLength ? cleaned : cleaned[..ReplyMaxLength].TrimEnd();
    }

    /// <summary>One customer waiting for an answer, with what the rules need to judge them.</summary>
    private sealed record WaitingConversation(
        long ConversationId,
        long TenantId,
        string ContactName,
        string LastInboundBody,
        ConversationMessageKind LastInboundKind,
        DateTimeOffset LastInboundAt,
        int InboundCount,
        int OutboundCount,
        int AutoRepliesToday,
        long? ConnectionId);

    [LoggerMessage(
        EventId = 2750,
        Level = LogLevel.Information,
        Message = "Automatic reply sent on conversation {ConversationId} for {Trigger}: {Status}.")]
    private partial void LogReplySent(long conversationId, string trigger, InboxMessageStatus status);

    [LoggerMessage(
        EventId = 2751,
        Level = LogLevel.Warning,
        Message = "The assistant could not write a reply for conversation {ConversationId}; the customer waits for a person.")]
    private partial void LogGenerationFailed(Exception exception, long conversationId);

    [LoggerMessage(
        EventId = 2752,
        Level = LogLevel.Information,
        Message = "Tenant {TenantId} has used its allowance of {MonthlyLimit} automatic replies; they are paused.")]
    private partial void LogAllowanceExhausted(long tenantId, int monthlyLimit);

    [LoggerMessage(
        EventId = 2754,
        Level = LogLevel.Information,
        Message = "The knowledge file did not answer conversation {ConversationId} ({Outcome}); holding message sent: {HandedOff}.")]
    private partial void LogNotCovered(long conversationId, string outcome, bool handedOff);

    [LoggerMessage(
        EventId = 2753,
        Level = LogLevel.Error,
        Message = "Automatic replies failed for tenant {TenantId}; other workspaces were unaffected.")]
    private partial void LogTenantFailed(Exception exception, long tenantId);
}
