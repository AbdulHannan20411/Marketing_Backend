using System.ComponentModel.DataAnnotations;

namespace Marketing.Infrastructure.WhatsApp;

/// <summary>Meta WhatsApp Cloud API settings, bound from the <c>WhatsApp</c> section.</summary>
public sealed class WhatsAppOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "WhatsApp";

    /// <summary>Graph API base address.</summary>
    [Required(AllowEmptyStrings = false)]
    public string BaseUrl { get; init; } = "https://graph.facebook.com";

    /// <summary>
    /// Graph API version, pinned rather than floating.
    /// <para>
    /// Meta ships breaking changes between versions and deprecates old ones on a published
    /// schedule. Pinning means an upgrade is a deliberate, testable change instead of an outage
    /// that arrives on Meta's timetable.
    /// </para>
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression(@"^v\d+\.\d+$", ErrorMessage = "Graph API version must look like 'v21.0'.")]
    public string ApiVersion { get; init; } = "v21.0";

    /// <summary>Meta app identifier used for Embedded Signup.</summary>
    [Required(AllowEmptyStrings = false)]
    public string AppId { get; init; } = string.Empty;

    /// <summary>
    /// Embedded Signup configuration identifier, from the app's Facebook Login for Business setup.
    /// </summary>
    /// <remarks>
    /// It names which signup flow the browser opens - which permissions are requested and which
    /// screens the customer sees. Not a secret: it is sent to the browser, which passes it to Meta.
    /// <para>
    /// Optional at startup, deliberately. A signup launched without one falls back to a generic
    /// login that returns no WhatsApp account - but the client already reads this value, finds it
    /// empty and disables the connect button with the reason shown, which is a better failure than
    /// an API that will not boot. Embedded Signup is also gated on Meta business verification, so
    /// an environment legitimately runs for weeks with no configuration to point at.
    /// </para>
    /// </remarks>
    public string ConfigId { get; init; } = string.Empty;

    /// <summary>
    /// Meta app secret. Used to exchange Embedded Signup codes and to verify webhook signatures.
    /// Supplied from the secret store, never committed.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string AppSecret { get; init; } = string.Empty;

    /// <summary>Token Meta echoes back when verifying the webhook subscription.</summary>
    [Required(AllowEmptyStrings = false)]
    public string WebhookVerifyToken { get; init; } = string.Empty;

    /// <summary>Request timeout for Graph API calls.</summary>
    [Range(1, 120)]
    public int RequestTimeoutSeconds { get; init; } = 30;
}
