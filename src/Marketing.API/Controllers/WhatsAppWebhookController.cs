using System.Text.Json;
using Asp.Versioning;
using Marketing.Application.DTOs.WhatsApp;
using Marketing.Application.Interfaces;
using Marketing.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Marketing.API.Controllers;

/// <summary>
/// Receives Meta's delivery receipts and template reviews.
/// <para>
/// The only anonymous write endpoint in the platform. Meta has no bearer token to present, so the
/// HMAC signature over the raw body is the entire authentication story - which is why the body is
/// read as bytes and verified before a single field is parsed.
/// </para>
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/whatsapp/webhook")]
[AllowAnonymous]
public sealed partial class WhatsAppWebhookController : ApiControllerBase
{
    /// <summary>
    /// Largest payload accepted, in bytes.
    /// <para>
    /// Meta batches receipts, so this is generous - but unbounded reading of an anonymous request
    /// body is how one request takes the process down.
    /// </para>
    /// </summary>
    private const int MaximumPayloadBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IWhatsAppWebhookVerifier _verifier;
    private readonly IWhatsAppWebhookService _webhooks;
    private readonly ILogger<WhatsAppWebhookController> _logger;

    /// <summary>Initialises a new instance.</summary>
    public WhatsAppWebhookController(
        IWhatsAppWebhookVerifier verifier,
        IWhatsAppWebhookService webhooks,
        ILogger<WhatsAppWebhookController> logger)
    {
        _verifier = verifier;
        _webhooks = webhooks;
        _logger = logger;
    }

    /// <summary>Answers Meta's subscription handshake.</summary>
    /// <remarks>
    /// Meta calls this once when the webhook is configured and expects the challenge echoed back
    /// as bare text - not JSON, and not inside the platform's response envelope.
    /// </remarks>
    /// <param name="mode">The <c>hub.mode</c> value.</param>
    /// <param name="verifyToken">The <c>hub.verify_token</c> value.</param>
    /// <param name="challenge">The <c>hub.challenge</c> value to echo.</param>
    /// <response code="200">The challenge, as plain text.</response>
    /// <response code="403">The verify token did not match.</response>
    [HttpGet]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult Verify(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        if (!_verifier.IsValidSubscription(mode, verifyToken))
        {
            LogHandshakeRejected();

            return Forbid();
        }

        return Content(challenge ?? string.Empty, "text/plain");
    }

    /// <summary>Accepts a webhook payload.</summary>
    /// <remarks>
    /// Answers 200 for anything signature-valid, including payloads it does not recognise. Meta
    /// retries a non-2xx for hours and disables a subscription that keeps failing, so a parse
    /// problem on this side must not be reported as one Meta should keep retrying.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The payload was accepted.</response>
    /// <response code="403">The signature did not match the app secret.</response>
    /// <response code="413">The payload exceeded the accepted size.</response>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> ReceiveAsync(CancellationToken cancellationToken)
    {
        var payload = await ReadBodyAsync(cancellationToken);

        if (payload is null)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (!_verifier.IsSignatureValid(payload, Request.Headers["X-Hub-Signature-256"].ToString()))
        {
            // Not logged with the payload. An unsigned request is unauthenticated input, and
            // writing it into the log is how a log becomes an injection surface of its own.
            LogSignatureRejected();

            return Forbid();
        }

        WebhookEnvelope? envelope;

        try
        {
            envelope = JsonSerializer.Deserialize<WebhookEnvelope>(payload, SerializerOptions);
        }
        catch (JsonException exception)
        {
            LogUnparseablePayload(exception);

            return Ok();
        }

        if (envelope is not null)
        {
            var applied = await _webhooks.ProcessAsync(envelope, cancellationToken);

            LogPayloadProcessed(applied);
        }

        return Ok();
    }

    /// <summary>Reads the raw body, refusing anything over the size ceiling.</summary>
    private async Task<byte[]?> ReadBodyAsync(CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();

        // Copied through a bounded buffer rather than trusting Content-Length, which an attacker
        // controls and which is absent entirely on a chunked request.
        var chunk = new byte[8192];
        int read;

        while ((read = await Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaximumPayloadBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return buffer.ToArray();
    }

    [LoggerMessage(
        EventId = 2811,
        Level = LogLevel.Warning,
        Message = "A WhatsApp webhook handshake was rejected: the verify token did not match.")]
    private partial void LogHandshakeRejected();

    [LoggerMessage(
        EventId = 2812,
        Level = LogLevel.Warning,
        Message = "A WhatsApp webhook payload was rejected: the signature did not match.")]
    private partial void LogSignatureRejected();

    [LoggerMessage(
        EventId = 2813,
        Level = LogLevel.Warning,
        Message = "A signature-valid WhatsApp webhook payload could not be parsed.")]
    private partial void LogUnparseablePayload(Exception exception);

    [LoggerMessage(
        EventId = 2814,
        Level = LogLevel.Information,
        Message = "Applied {ReceiptCount} WhatsApp delivery receipts.")]
    private partial void LogPayloadProcessed(int receiptCount);
}
