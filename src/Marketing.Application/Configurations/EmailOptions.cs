using System.ComponentModel.DataAnnotations;

namespace Marketing.Application.Configurations;

/// <summary>Email settings, bound from the <c>Email</c> configuration section.</summary>
public sealed class EmailOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Email";

    /// <summary>Address messages are sent from.</summary>
    [Required(AllowEmptyStrings = false)]
    public string FromAddress { get; init; } = "no-reply@marketing-platform.io";

    /// <summary>Display name messages are sent from.</summary>
    public string FromName { get; init; } = "Marketing Platform";

    /// <summary>
    /// Base URL of the Angular client, used to build invitation and reset links.
    /// <para>
    /// Configured rather than derived from the request, because a link built from an inbound
    /// <c>Host</c> header is a host-header injection: an attacker who can set that header gets the
    /// platform to email a reset link pointing at their own site.
    /// </para>
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string ClientBaseUrl { get; init; } = "http://localhost:4200";

    /// <summary>How long an invitation link stays valid.</summary>
    [Range(1, 336)]
    public int InvitationLifetimeHours { get; init; } = 72;

    /// <summary>
    /// How long a password-reset link stays valid.
    /// <para>
    /// Deliberately much shorter than an invitation. A reset link is a live credential sitting in
    /// an inbox, and the window in which a compromised mailbox is useful should be small.
    /// </para>
    /// </summary>
    [Range(1, 24)]
    public int PasswordResetLifetimeHours { get; init; } = 1;
}
