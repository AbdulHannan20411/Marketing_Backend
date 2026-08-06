using Marketing.DataAccess.Entities;

namespace Marketing.Business.Repositories.Interfaces;

/// <summary>Refresh-token session queries.</summary>
public interface IRefreshTokenRepository : IRepository<RefreshToken>
{
    /// <summary>
    /// Loads a tracked token by its hash.
    /// <para>
    /// Bypasses the tenant filter for the same reason the sign-in lookup does: a refresh request
    /// carries no valid bearer token, so there is no ambient tenant yet. The lookup key is a
    /// 256-bit hash the caller must already possess, so it discloses nothing.
    /// </para>
    /// </summary>
    /// <param name="tokenHash">Hex-encoded SHA-256 hash of the presented token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>Loads every live token issued for a session.</summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<RefreshToken>> FindBySessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a user's currently usable sessions, tracked and newest first, so the caller can
    /// enforce a concurrent-session budget.
    /// </summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="utcNow">Instant used to exclude expired tokens.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<RefreshToken>> GetActiveSessionsAsync(
        Guid userId,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes every unexpired token for a user - the "sign out everywhere" path, and the response
    /// to detecting a replayed token.
    /// </summary>
    /// <param name="userId">User whose sessions are revoked.</param>
    /// <param name="reason">Reason recorded on each token.</param>
    /// <param name="utcNow">Instant to stamp.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of tokens revoked.</returns>
    public Task<int> RevokeAllForUserAsync(
        Guid userId,
        string reason,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently removes tokens that expired before a cut-off.
    /// <para>
    /// A hard delete, unlike everywhere else in the schema: an expired token is not business data,
    /// it is spent credential material, and retaining it indefinitely only grows the attack surface
    /// and the table.
    /// </para>
    /// </summary>
    /// <param name="expiredBefore">Cut-off instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of rows deleted.</returns>
    public Task<int> PurgeExpiredAsync(DateTimeOffset expiredBefore, CancellationToken cancellationToken = default);
}
