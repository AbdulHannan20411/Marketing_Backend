namespace Marketing.Application.Services;

/// <summary>A Meta send failure, worded for whoever reads the campaign report.</summary>
/// <param name="Reason">What happened, and what to do about it where there is something to do.</param>
/// <param name="Permanent">Whether sending the same message again can only fail the same way.</param>
internal sealed record MetaSendError(string Reason, bool Permanent);

/// <summary>Plain-language reasons for the Cloud API error codes a campaign send can hit.</summary>
/// <remarks>
/// A failure used to be recorded as "Graph API returned 400. Code 131058, trace ..." - accurate, and
/// no help to anyone deciding what to fix. Only codes whose meaning is stable are listed; anything
/// else keeps the raw message, which at least carries the code and trace for support.
/// <para>
/// Permanent means Meta will give the same answer however often it is asked: a test-only template, a
/// number not on WhatsApp. Those are written off at once rather than retried. Rate limits are not
/// permanent - the same message can go through a little later.
/// </para>
/// </remarks>
internal static class MetaSendErrors
{
    private static readonly Dictionary<int, MetaSendError> Known = new()
    {
        [190] = Permanent(
            "Meta rejected the connection's access token. Reconnect WhatsApp, then resume the campaign."),
        [368] = Permanent("Meta has temporarily blocked this WhatsApp account for policy violations."),
        [131008] = Permanent("A required message parameter was missing."),
        [131009] = Permanent(
            "Meta refused a parameter value - most often a phone number that is not in international format."),
        [131021] = Permanent("The recipient is the sending number itself."),
        [131026] = Permanent(
            "Message undeliverable. The number may not use WhatsApp, or has not accepted WhatsApp's latest terms."),
        [131030] = Permanent(
            "This recipient is not on the test number's allowed list. Add it in the Meta app dashboard."),
        [131031] = Permanent("Meta has locked or restricted this WhatsApp Business Account."),
        [131037] = Permanent(
            "Meta has not approved this number's display name yet, so nothing can be sent from it. "
            + "Check the name under WhatsApp Manager - Phone numbers; approval usually takes a day or two."),
        [131042] = Permanent(
            "Meta could not charge this WhatsApp Business Account. Check its payment method in Meta Business Settings."),
        [131047] = Permanent(
            "More than 24 hours have passed since this person last messaged you, so only an approved template can be sent."),
        [131049] = Permanent(
            "Meta held back this marketing message to protect the recipient's experience. It can be sent again later."),
        [131050] = Permanent("The recipient has turned off marketing messages from your business."),
        [131051] = Permanent("Meta does not support this message type."),
        [131058] = Permanent(
            "\"hello_world\" can only be sent from Meta's public test numbers. Choose one of your own approved templates."),
        [132000] = Permanent("The number of template variables does not match the approved template."),
        [132001] = Permanent(
            "This template does not exist in that language on the connected WhatsApp account. Sync templates and choose an approved one."),
        [132005] = Permanent("The template's text is too long once its variables are filled in."),
        [132007] = Permanent("The template's content breaks WhatsApp policy."),
        [132012] = Permanent("A template variable is in the wrong format."),
        [132015] = Permanent("Meta has paused this template because of low quality."),
        [132016] = Permanent("Meta has disabled this template because of low quality."),
        [133010] = Permanent("The sending number is not registered with the WhatsApp Cloud API."),

        [80007] = Retryable("This WhatsApp Business Account reached Meta's rate limit."),
        [130429] = Retryable("Meta's sending rate limit was reached."),
        [131048] = Retryable(
            "Meta limited sending because recent messages were blocked or reported by recipients."),
        [131056] = Retryable("Too many messages were sent to the same recipient in a short time."),
    };

    /// <summary>What a Meta error code means, or null when it is not one listed here.</summary>
    /// <param name="code">Meta's <c>error.code</c>, if the failure carried one.</param>
    public static MetaSendError? Describe(int? code) =>
        code is { } value && Known.TryGetValue(value, out var error) ? error : null;

    private static MetaSendError Permanent(string reason) => new(reason, Permanent: true);

    private static MetaSendError Retryable(string reason) => new(reason, Permanent: false);
}
