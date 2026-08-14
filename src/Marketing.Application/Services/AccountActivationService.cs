using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Application.Configurations;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Options;
using static Marketing.Common.Constants.AppConstants;

namespace Marketing.Application.Services;

/// <summary>
/// Who caused a workspace-originated email, so the recipient can see it.
/// </summary>
/// <remarks>
/// Employees are invited by a colleague, but the mail leaves the platform's SMTP server. Without
/// naming the human behind it the recipient sees an address they do not recognise for a message
/// they were expecting from someone they do — which reads as spam, and gets ignored or reported.
/// <para>
/// This never changes the <c>From:</c> header. Sending as the customer's own domain would fail
/// that domain's SPF and DKIM checks; genuinely sending as them needs per-tenant verified sending
/// domains, which is a much larger piece of work.
/// </para>
/// </remarks>
/// <param name="SenderName">Display name of the person who acted.</param>
/// <param name="SenderEmail">Their address, used for <c>Reply-To</c>.</param>
public sealed record EmailAttribution(string SenderName, string SenderEmail);

/// <summary>Issues and delivers the single-use links that activate accounts and reset passwords.</summary>
public interface IAccountActivationService
{
    /// <summary>
    /// Issues an invitation and emails the activation link.
    /// <para>
    /// Best-effort delivery. An invitation whose email failed to send is still a created account,
    /// and rolling the account back because a mail server was down would be worse; the admin can
    /// re-invite.
    /// </para>
    /// </summary>
    /// <param name="user">The invited user. Must already be persisted.</param>
    /// <param name="organisationName">Organisation name, used in the message.</param>
    /// <param name="attribution">
    /// Who caused the invitation. Named in the subject and body and used for <c>Reply-To</c>; null
    /// sends an unattributed message rather than one signed by nobody.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SendInvitationAsync(
        User user,
        string? organisationName,
        EmailAttribution? attribution = null,
        CancellationToken cancellationToken = default);

    /// <summary>Issues a password reset and emails the link.</summary>
    /// <param name="user">The user resetting their password.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SendPasswordResetAsync(User user, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAccountActivationService" />
public sealed class AccountActivationService : IAccountActivationService
{
    private readonly IRepository<UserToken> _tokens;
    private readonly IQueryExecutor _queries;
    private readonly ITokenService _tokenService;
    private readonly IEmailSender _email;
    private readonly IRequestContext _requestContext;
    private readonly EmailOptions _options;

    /// <summary>Initialises a new instance.</summary>
    public AccountActivationService(
        IRepository<UserToken> tokens,
        IQueryExecutor queries,
        ITokenService tokenService,
        IEmailSender email,
        IRequestContext requestContext,
        IOptions<EmailOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _tokens = tokens;
        _queries = queries;
        _tokenService = tokenService;
        _email = email;
        _requestContext = requestContext;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task SendInvitationAsync(
        User user,
        string? organisationName,
        EmailAttribution? attribution = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var token = await IssueAsync(
            user,
            UserTokenPurpose.Invitation,
            TimeSpan.FromHours(_options.InvitationLifetimeHours),
            cancellationToken);

        var link = BuildLink("accept-invitation", token.Value);
        var workspace = string.IsNullOrWhiteSpace(organisationName) ? "the platform" : organisationName;
        var sender = attribution?.SenderName;

        // Named in the subject, because the recipient is deciding whether to trust a message that
        // arrives from an address they have never seen. "Amara Chen invited you…" is recognisable;
        // "You have been invited" from a no-reply address reads as spam and gets reported.
        // Header-sanitised, not HTML-escaped. A subject line is text, not markup: escaping it would
        // put a literal "&amp;" in the recipient's inbox for a workspace called "Smith & Co". What a
        // header genuinely cannot contain is a line break, which would let a tenant-supplied name
        // split the subject and inject headers of its own.
        var subject = string.IsNullOrWhiteSpace(sender)
            ? Header($"You have been invited to join {workspace} on {_options.FromName}")
            : Header($"{sender} invited you to join {workspace} on {_options.FromName}");

        var attributionHtml = attribution is { } who
            ? $"<p style=\"color:#6b7280;font-size:12px\">This invitation was sent by "
              + $"{Escape(who.SenderName)} ({Escape(who.SenderEmail)}) through {Escape(_options.FromName)}. "
              + "If you weren't expecting it, you can ignore this email.</p>"
            : string.Empty;

        var attributionText = attribution is { } signer
            ? $"{Environment.NewLine}{Environment.NewLine}This invitation was sent by "
              + $"{signer.SenderName} ({signer.SenderEmail}) through "
              + $"{_options.FromName}. If you weren't expecting it, you can ignore this email."
            : string.Empty;

        var opening = string.IsNullOrWhiteSpace(sender)
            ? $"You have been invited to work in <strong>{Escape(workspace)}</strong>."
            : $"<strong>{Escape(sender)}</strong> has invited you to work in "
              + $"<strong>{Escape(workspace)}</strong> on {Escape(_options.FromName)}.";

        // Built as HTML directly rather than going through the plain-text wrapper, because every
        // interpolated value here — the sender's name, their address, the workspace name — is
        // supplied by a tenant. An organisation renamed to a script tag must not be able to inject
        // anything into a message delivered to somebody else's inbox.
        var html = $"<p>Hello {Escape(user.DisplayName)},</p>"
                   + $"<p>{opening} Set your password to get started.</p>"
                   + $"<p><a href=\"{Escape(link)}\">Set your password</a></p>"
                   + $"<p>This link can be used once and expires in "
                   + $"{_options.InvitationLifetimeHours} hours.</p>"
                   + attributionHtml;

        var text = $"""
             Hello {user.DisplayName},

             {(string.IsNullOrWhiteSpace(sender) ? $"You have been invited to work in {workspace}." : $"{sender} has invited you to work in {workspace} on {_options.FromName}.")} Set your password to get started:
             {link}

             This link can be used once and expires in {_options.InvitationLifetimeHours} hours.
             """ + attributionText;

        await _email.SendAsync(
            new EmailMessage(user.Email, user.DisplayName, subject, html, text)
            {
                // Replies reach the person who caused the message, not a mailbox nobody reads.
                ReplyToAddress = attribution?.SenderEmail,
                ReplyToName = attribution?.SenderName,
            },
            cancellationToken);
    }

    /// <summary>Escapes a tenant-supplied value before it enters an HTML body.</summary>
    private static string Escape(string? value) => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    /// <summary>
    /// Makes a tenant-supplied value safe to put in a header.
    /// </summary>
    /// <remarks>
    /// Strips the control characters that terminate a header line. Without this, a workspace named
    /// with an embedded newline could append headers of its own to a message the platform sends on
    /// somebody else's behalf.
    /// </remarks>
    private static string Header(string value) =>
        new([.. value.Where(character => !char.IsControl(character))]);

    /// <inheritdoc />
    public async Task SendPasswordResetAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var token = await IssueAsync(
            user,
            UserTokenPurpose.PasswordReset,
            TimeSpan.FromHours(_options.PasswordResetLifetimeHours),
            cancellationToken);

        var link = BuildLink("reset-password", token.Value);

        await SendAsync(
            user,
            "Reset your password",
            $"""
             Hello {user.DisplayName},

             Someone asked to reset the password for this account.

             Choose a new password:
             {link}

             This link can be used once and expires in {_options.PasswordResetLifetimeHours} hour(s).
             If this was not you, no action is needed - your password has not changed.
             """,
            cancellationToken);
    }

    /// <summary>
    /// Creates a token, invalidating any outstanding one of the same purpose first.
    /// <para>
    /// Superseding matters: without it, requesting three resets leaves three live links in an
    /// inbox, and the oldest stays usable for its full lifetime.
    /// </para>
    /// </summary>
    private async Task<SecureTokenResult> IssueAsync(
        User user,
        UserTokenPurpose purpose,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        var outstanding = await _queries.ToListAsync(
            _tokens.Query(asNoTracking: false)
                .Where(token => token.UserId == user.Id
                                && token.Purpose == purpose
                                && token.ConsumedOn == null),
            cancellationToken);

        foreach (var token in outstanding)
        {
            _tokens.Remove(token);
        }

        var material = _tokenService.CreateSecureToken(lifetime);

        _tokens.Add(new UserToken
        {
            TenantId = user.TenantId,

            // By navigation, because this is also called for a user created in the same unit of
            // work - a new admin account being invited - whose key does not exist yet. For a user
            // already in the database it resolves to the same thing.
            User = user,
            Purpose = purpose,
            TokenHash = material.Hash,
            ExpiresOn = material.ExpiresAtUtc,
            RequestedByIp = _requestContext.IpAddress,
        });

        // Left uncommitted deliberately. The caller owns the transaction, so the token, the user
        // and anything else in the same operation commit together or not at all.
        return new SecureTokenResult(material.Value);
    }

    private string BuildLink(string path, string token) =>
        $"{_options.ClientBaseUrl.TrimEnd('/')}/auth/{path}?token={Uri.EscapeDataString(token)}";

    private async Task SendAsync(User user, string subject, string body, CancellationToken cancellationToken)
    {
        var html = $"<p>{body.Replace("\n", "<br />", StringComparison.Ordinal)}</p>";

        await _email.SendAsync(
            new EmailMessage(user.Email, user.DisplayName, subject, html, body),
            cancellationToken);
    }

    /// <summary>The plaintext token, which exists only long enough to go into a link.</summary>
    private sealed record SecureTokenResult(string Value);
}
