using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Application.Configurations;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Options;
using static Marketing.Common.Constants.AppConstants;

namespace Marketing.Application.Services;

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
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SendInvitationAsync(User user, string? organisationName, CancellationToken cancellationToken = default);

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

        await SendAsync(
            user,
            $"You have been invited to {workspace}",
            $"""
             Hello {user.DisplayName},

             You have been invited to join {workspace}.

             Set your password and activate your account:
             {link}

             This link can be used once and expires in {_options.InvitationLifetimeHours} hours.
             If you were not expecting this invitation you can ignore this message.
             """,
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
