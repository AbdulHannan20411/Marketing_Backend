using System.Net.Http.Headers;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Marketing.Infrastructure.WhatsApp.Clients;

/// <summary>
/// Attaches the calling tenant's Meta access token to every Graph API request.
/// <para>
/// Done in a handler rather than at each call site so no future caller can forget it, and so the
/// decrypted token exists only for the duration of one request rather than being passed around as
/// a parameter.
/// </para>
/// <para>
/// The tenant comes from <c>ITenantContext</c> - the same claim-derived source everything else
/// uses - so a request can only ever be authenticated as the tenant it belongs to. There is no
/// path by which a caller can select which token is used.
/// </para>
/// </summary>
public sealed partial class TenantAccessTokenHandler : DelegatingHandler
{
    /// <summary>
    /// The one Graph endpoint that must not carry a tenant token: it is authenticated by the app
    /// secret and is how a tenant obtains a token in the first place.
    /// </summary>
    private const string TokenExchangePath = "oauth/access_token";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TenantAccessTokenHandler> _logger;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="scopeFactory">
    /// Used to resolve the connection repository per send.
    /// <para>
    /// The factory pools message handlers for minutes at a time and builds them from a scope that
    /// lives just as long, so a repository injected straight into this constructor would hold one
    /// <c>DbContext</c> open across thousands of unrelated requests. A scope per send is the cost
    /// of not doing that.
    /// </para>
    /// </param>
    /// <param name="logger">Logger.</param>
    public TenantAccessTokenHandler(IServiceScopeFactory scopeFactory, ILogger<TenantAccessTokenHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestUri?.AbsolutePath.Contains(TokenExchangePath, StringComparison.Ordinal) == true)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();

        // Resolved inside the scope but read here: the tenant itself comes from an ambient source -
        // the request's claims or a job's explicit scope - which flows across the new scope
        // unchanged. Only the database work needs the fresh scope.
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        if (tenantContext.TenantId is { } tenantId)
        {
            var connections = scope.ServiceProvider.GetRequiredService<IWhatsAppConnectionRepository>();
            var connection = await connections.FindForTenantAsync(tenantId, cancellationToken);

            if (connection?.EncryptedAccessToken is { Length: > 0 } encrypted)
            {
                var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();

                try
                {
                    request.Headers.Authorization =
                        new AuthenticationHeaderValue("Bearer", protector.Unprotect(encrypted));
                }
                catch (InvalidOperationException exception)
                {
                    // A token that cannot be decrypted - wrong key, tampered row - must not be sent
                    // as an empty header. Letting Meta return 401 makes the real cause invisible.
                    LogTokenUnreadable(exception, tenantId);
                    throw;
                }
            }
        }

        // Deliberately proceeds without a header when there is no connection. Meta answers 401 and
        // GraphApiErrorHandler turns it into a typed failure, which is a clearer story than a
        // half-configured request failing somewhere else.
        return await base.SendAsync(request, cancellationToken);
    }

    [LoggerMessage(
        EventId = 2501,
        Level = LogLevel.Error,
        Message = "The stored Meta access token for tenant {TenantId} could not be decrypted.")]
    private partial void LogTokenUnreadable(Exception exception, Guid tenantId);
}
