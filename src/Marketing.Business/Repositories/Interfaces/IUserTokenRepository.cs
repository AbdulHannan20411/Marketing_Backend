using Marketing.DataAccess.Entities;

namespace Marketing.Business.Repositories.Interfaces;

/// <summary>
/// Looks up single-use tokens by their hash.
/// </summary>
/// <remarks>
/// Its own repository because redemption happens <b>before</b> the caller has any tenant. Accepting
/// an invitation or resetting a password is an anonymous request, so a token row belonging to a
/// tenant is invisible to the global query filter — the lookup returns nothing and the caller is
/// told their perfectly valid link is invalid.
/// <para>
/// Ignoring the tenant filter is safe here, and is the only correct order of operations: the hash
/// <em>is</em> the credential. It is a 256-bit secret matched exactly, and the tenant is a result of
/// resolving it rather than an input to it. Requiring tenant context first is backwards — the token
/// is what establishes which tenant the caller belongs to.
/// </para>
/// </remarks>
public interface IUserTokenRepository : IRepository<UserToken>
{
    /// <summary>
    /// Finds a live token by its hash, tracked and ready to be consumed.
    /// </summary>
    /// <param name="tokenHash">Hash of the presented token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The token, or null when no live row carries that hash.</returns>
    public Task<UserToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken = default);
}
