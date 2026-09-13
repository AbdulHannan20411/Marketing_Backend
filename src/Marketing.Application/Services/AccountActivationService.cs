using Marketing.Application.Services.Email;
using System.Globalization;
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

    /// <summary>
    /// Welcomes the owner of a workspace that has just become active.
    /// </summary>
    /// <remarks>
    /// Sent once, when a workspace's first administrator accepts their invitation. Employees joining
    /// an active workspace are not welcomed again; their invitation already did that.
    /// </remarks>
    /// <param name="user">The administrator who activated the workspace.</param>
    /// <param name="workspaceName">The workspace's name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SendWelcomeAsync(User user, string workspaceName, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAccountActivationService" />
public sealed class AccountActivationService : IAccountActivationService
{
    private readonly IRepository<UserToken> _tokens;
    private readonly IQueryExecutor _queries;
    private readonly ITokenService _tokenService;
    private readonly IEmailSender _email;
    private readonly IEmailTemplateRenderer _templates;
    private readonly IRequestContext _requestContext;
    private readonly EmailOptions _options;

    /// <summary>Initialises a new instance.</summary>
    public AccountActivationService(
        IRepository<UserToken> tokens,
        IQueryExecutor queries,
        ITokenService tokenService,
        IEmailSender email,
        IEmailTemplateRenderer templates,
        IRequestContext requestContext,
        IOptions<EmailOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _tokens = tokens;
        _queries = queries;
        _tokenService = tokenService;
        _email = email;
        _templates = templates;
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

        // Values only. The wording, including the subject that names the inviter, now lives in the
        // auth.invitation template; the renderer encodes every value for the HTML body and strips
        // control characters from the subject, which is what the hand-built version did by hand.
        var message = await _templates.RenderAsync(
            "auth.invitation",
            user.Email,
            user.DisplayName,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = user.DisplayName,
                ["inviterName"] = attribution?.SenderName ?? string.Empty,
                ["inviterEmail"] = attribution?.SenderEmail ?? string.Empty,
                ["workspaceName"] = string.IsNullOrWhiteSpace(organisationName) ? "the platform" : organisationName,
                ["actionUrl"] = link,
                ["expiresInHours"] = _options.InvitationLifetimeHours.ToString(CultureInfo.InvariantCulture),
            },
            cancellationToken);

        await _email.SendAsync(
            message with
            {
                // Replies reach the person who caused the message, not a mailbox nobody reads.
                ReplyToAddress = attribution?.SenderEmail,
                ReplyToName = attribution?.SenderName,
            },
            cancellationToken);
    }

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

        var message = await _templates.RenderAsync(
            "auth.password_reset",
            user.Email,
            user.DisplayName,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = user.DisplayName,
                ["actionUrl"] = link,
                ["expiresInHours"] = _options.PasswordResetLifetimeHours.ToString(CultureInfo.InvariantCulture),
            },
            cancellationToken);

        await _email.SendAsync(message, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendWelcomeAsync(User user, string workspaceName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var message = await _templates.RenderAsync(
            "auth.welcome",
            user.Email,
            user.DisplayName,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = user.DisplayName,
                ["workspaceName"] = workspaceName,
                ["actionUrl"] = $"{_options.ClientBaseUrl.TrimEnd('/')}/dashboard",
            },
            cancellationToken);

        await _email.SendAsync(message, cancellationToken);
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

    /// <summary>The plaintext token, which exists only long enough to go into a link.</summary>
    private sealed record SecureTokenResult(string Value);
}
