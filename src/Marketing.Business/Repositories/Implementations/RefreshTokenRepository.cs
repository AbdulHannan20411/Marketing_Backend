using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Context;
using Marketing.DataAccess.Entities;
using Microsoft.EntityFrameworkCore;

namespace Marketing.Business.Repositories.Implementations;

/// <summary>Entity Framework implementation of <see cref="IRefreshTokenRepository"/>.</summary>
public sealed class RefreshTokenRepository : Repository<RefreshToken>, IRefreshTokenRepository
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="context">Database context.</param>
    public RefreshTokenRepository(ApplicationDbContext context)
        : base(context)
    {
    }

    /// <inheritdoc />
    public Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken = default) =>
        Set
            .IgnoreQueryFilters()
            .Where(token => !token.IsDeleted && token.TokenHash == tokenHash)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RefreshToken>> FindBySessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        await Set
            .IgnoreQueryFilters()
            .Where(token => !token.IsDeleted && token.SessionId == sessionId)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RefreshToken>> GetActiveSessionsAsync(
        Guid userId,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default) =>
        await Set
            .IgnoreQueryFilters()
            .Where(token =>
                !token.IsDeleted
                && token.UserId == userId
                && token.RevokedOn == null
                && token.ConsumedOn == null
                && token.ExpiresOn > utcNow)
            .OrderByDescending(token => token.CreatedOn)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public Task<int> RevokeAllForUserAsync(
        Guid userId,
        string reason,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default) =>
        // A set-based update rather than load-mutate-save: revocation is a security action that
        // should complete in one statement, and a user with many sessions must not turn it into a
        // long-running loop of round trips.
        Set
            .IgnoreQueryFilters()
            .Where(token =>
                !token.IsDeleted
                && token.UserId == userId
                && token.RevokedOn == null
                && token.ExpiresOn > utcNow)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(token => token.RevokedOn, utcNow)
                    .SetProperty(token => token.RevokedReason, reason)
                    .SetProperty(token => token.ModifiedOn, utcNow),
                cancellationToken);

    /// <inheritdoc />
    public Task<int> PurgeExpiredAsync(
        DateTimeOffset expiredBefore,
        CancellationToken cancellationToken = default) =>
        Set
            .IgnoreQueryFilters()
            .Where(token => token.ExpiresOn < expiredBefore)
            .ExecuteDeleteAsync(cancellationToken);
}
