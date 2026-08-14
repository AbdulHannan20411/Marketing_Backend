using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Context;
using Marketing.DataAccess.Entities;
using Microsoft.EntityFrameworkCore;

namespace Marketing.Business.Repositories.Implementations;

/// <inheritdoc cref="IUserTokenRepository" />
public sealed class UserTokenRepository : Repository<UserToken>, IUserTokenRepository
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="context">Database context.</param>
    public UserTokenRepository(ApplicationDbContext context)
        : base(context)
    {
    }

    /// <inheritdoc />
    public Task<UserToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken = default) =>
        Set
            // The tenant filter is bypassed deliberately; see the interface. The soft-delete check
            // is kept, because a revoked token must stay unusable.
            .IgnoreQueryFilters()
            .Where(token => !token.IsDeleted && token.TokenHash == tokenHash)

            // Tracked: the caller stamps ConsumedOn on the row it gets back, so handing out a
            // detached copy would silently discard the consumption.
            .FirstOrDefaultAsync(cancellationToken);
}
