using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Marketing.Application.Interfaces;
using Marketing.Common.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Marketing.Infrastructure.Ai;

/// <summary>
/// <see cref="IAiService"/> backed by the Gemini API's <c>models.generateContent</c>.
/// </summary>
/// <remarks>
/// <para>
/// A typed <see cref="HttpClient"/> rather than the Google GenAI SDK, matching
/// <c>GooglePlacesProvider</c>: one POST and one response shape do not justify a second way of
/// calling Google, and it keeps retries, timeouts and logging under the same conventions as every
/// other provider here.
/// </para>
/// <para>
/// The key travels in the <c>x-goog-api-key</c> header, never the query string, so it cannot appear
/// in a logged URL. Neither the key nor the prompt is ever logged; the prompt's length is.
/// </para>
/// </remarks>
public sealed partial class GeminiAiService : IAiService
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";
    private const string ServiceName = "Gemini";

    /// <summary>Frames every request. Kept short: it is sent, and paid for, on every call.</summary>
    private const string SystemInstruction =
        "You are a marketing assistant for small businesses. Write clear, friendly, ready-to-use "
        + "marketing copy. Answer in plain text without Markdown formatting. Keep promotional messages "
        + "concise enough to send on WhatsApp unless the user asks for something longer.";

    /// <summary>Finish reasons meaning the provider withheld the answer on policy grounds.</summary>
    private static readonly HashSet<string> BlockedFinishReasons =
        new(["SAFETY", "PROHIBITED_CONTENT", "BLOCKLIST", "SPII", "RECITATION"], StringComparer.Ordinal);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _client;
    private readonly GeminiOptions _options;
    private readonly ILogger<GeminiAiService> _logger;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="client">HTTP client.</param>
    /// <param name="options">Provider settings, including the key.</param>
    /// <param name="logger">Logger.</param>
    public GeminiAiService(HttpClient client, IOptions<GeminiOptions> options, ILogger<GeminiAiService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    /// <inheritdoc />
    public async Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        if (!IsConfigured)
        {
            LogNotConfigured();
            throw new BusinessRuleException(
                "ai_not_configured",
                "The AI assistant is not available yet. Please contact your administrator.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{BaseUrl}{Uri.EscapeDataString(_options.Model)}:generateContent")
        {
            Content = JsonContent.Create(
                new GenerateContentRequest(
                    new Content(null, [new Part(SystemInstruction, null)]),
                    [new Content("user", [new Part(prompt, null)])],
                    new GenerationConfig(_options.MaxOutputTokens)),
                options: Json),
        };
        request.Headers.Add("x-goog-api-key", _options.ApiKey);

        // Our own deadline, linked to the caller's, so a timeout and a user leaving the page can be
        // told apart: only the second is allowed to surface as a cancellation.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        var started = Stopwatch.GetTimestamp();

        try
        {
            using var response = await _client.SendAsync(request, deadline.Token);

            if (!response.IsSuccessStatusCode)
            {
                throw await FailureAsync(response, deadline.Token);
            }

            var payload = await response.Content.ReadFromJsonAsync<GenerateContentResponse>(Json, deadline.Token);

            return Extract(payload, Stopwatch.GetElapsedTime(started), prompt.Length);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogTimedOut(_options.Model, _options.TimeoutSeconds);
            throw new ExternalServiceException(ServiceName, "The AI provider did not respond in time.", isTransient: true);
        }
        catch (HttpRequestException exception)
        {
            LogUnreachable(exception, _options.Model);
            throw new ExternalServiceException(
                ServiceName, "The AI provider could not be reached.", exception, isTransient: true);
        }
        catch (JsonException exception)
        {
            LogMalformed(exception, _options.Model);
            throw new ExternalServiceException(ServiceName, "The AI provider returned an unreadable response.", exception);
        }
    }

    /// <summary>Turns a provider error status into a platform exception.</summary>
    /// <remarks>
    /// Only Google's status name (for example <c>INVALID_ARGUMENT</c>) is logged from the body. It is
    /// enough to tell an invalid key from an unknown model, and carries nothing sensitive.
    /// </remarks>
    private async Task<AppException> FailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var providerStatus = await ReadProviderStatusAsync(response, cancellationToken);

        LogProviderFailed(status, providerStatus, _options.Model);

        return response.StatusCode switch
        {
            // The platform's free-tier allowance, which does come back after a minute.
            HttpStatusCode.TooManyRequests => new RateLimitException(
                "ai_rate_limited",
                "The AI assistant is busy right now. Please try again in a minute."),

            _ when status >= 500 => new ExternalServiceException(
                ServiceName, $"The AI provider failed with {status}.", isTransient: true),

            // 400 with API_KEY_INVALID, 403 for a restricted key, 404 for an unknown model: all
            // configuration problems an operator fixes, never something the user can retry past.
            _ => new ExternalServiceException(
                ServiceName, $"The AI provider rejected the request with {status} ({providerStatus})."),
        };
    }

    private static async Task<string> ReadProviderStatusAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(Json, cancellationToken);
            return error?.Error?.Status ?? "unknown";
        }
        catch (JsonException)
        {
            return "unknown";
        }
    }

    private string Extract(GenerateContentResponse? payload, TimeSpan elapsed, int promptLength)
    {
        if (payload is null)
        {
            LogEmpty(_options.Model, "no body");
            throw new ExternalServiceException(ServiceName, "The AI provider returned no response.");
        }

        if (payload.PromptFeedback?.BlockReason is { } blockReason)
        {
            LogBlocked(_options.Model, blockReason);
            throw Blocked();
        }

        var candidate = payload.Candidates?.FirstOrDefault();

        // Thinking models can return their reasoning as parts marked `thought`; only the answer is
        // the user's.
        var text = string.Concat(
            candidate?.Content?.Parts?
                .Where(part => part.Thought != true)
                .Select(part => part.Text) ?? []).Trim();

        if (text.Length == 0)
        {
            if (candidate?.FinishReason is { } reason && BlockedFinishReasons.Contains(reason))
            {
                LogBlocked(_options.Model, reason);
                throw Blocked();
            }

            LogEmpty(_options.Model, candidate?.FinishReason ?? "no candidate");
            throw new ExternalServiceException(ServiceName, "The AI provider returned an empty response.");
        }

        LogGenerated(_options.Model, (long)elapsed.TotalMilliseconds, promptLength, text.Length, candidate?.FinishReason);

        return text;
    }

    private static BusinessRuleException Blocked() =>
        new("ai_response_blocked", "The assistant couldn't answer that request. Try rewording it.");

    [LoggerMessage(EventId = 3701, Level = LogLevel.Warning,
        Message = "AI assistant called but no Gemini API key is configured (Gemini:ApiKey or GEMINI_API_KEY).")]
    private partial void LogNotConfigured();

    [LoggerMessage(EventId = 3702, Level = LogLevel.Warning,
        Message = "Gemini returned {StatusCode} ({ProviderStatus}) for model {Model}.")]
    private partial void LogProviderFailed(int statusCode, string providerStatus, string model);

    [LoggerMessage(EventId = 3703, Level = LogLevel.Warning,
        Message = "Gemini model {Model} did not respond within {TimeoutSeconds} seconds.")]
    private partial void LogTimedOut(string model, int timeoutSeconds);

    [LoggerMessage(EventId = 3704, Level = LogLevel.Warning, Message = "Gemini could not be reached for model {Model}.")]
    private partial void LogUnreachable(Exception exception, string model);

    [LoggerMessage(EventId = 3705, Level = LogLevel.Warning, Message = "Gemini returned malformed JSON for model {Model}.")]
    private partial void LogMalformed(Exception exception, string model);

    [LoggerMessage(EventId = 3706, Level = LogLevel.Warning, Message = "Gemini returned no text for model {Model}: {Reason}.")]
    private partial void LogEmpty(string model, string reason);

    [LoggerMessage(EventId = 3707, Level = LogLevel.Information, Message = "Gemini withheld an answer for model {Model}: {Reason}.")]
    private partial void LogBlocked(string model, string reason);

    [LoggerMessage(EventId = 3708, Level = LogLevel.Information,
        Message = "Gemini model {Model} answered in {ElapsedMs} ms (prompt {PromptLength} chars, answer {AnswerLength} chars, finish {FinishReason}).")]
    private partial void LogGenerated(string model, long elapsedMs, int promptLength, int answerLength, string? finishReason);

    private sealed record GenerateContentRequest(
        [property: JsonPropertyName("systemInstruction")] Content SystemInstruction,
        [property: JsonPropertyName("contents")] IReadOnlyList<Content> Contents,
        [property: JsonPropertyName("generationConfig")] GenerationConfig GenerationConfig);

    private sealed record Content(
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("parts")] IReadOnlyList<Part>? Parts);

    private sealed record Part(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("thought")] bool? Thought);

    private sealed record GenerationConfig(
        [property: JsonPropertyName("maxOutputTokens")] int MaxOutputTokens);

    private sealed record GenerateContentResponse(List<Candidate>? Candidates, PromptFeedback? PromptFeedback);

    private sealed record Candidate(Content? Content, string? FinishReason);

    private sealed record PromptFeedback(string? BlockReason);

    private sealed record ErrorResponse(ErrorBody? Error);

    private sealed record ErrorBody(int? Code, string? Status);
}
