using Marketing.DataAccess.Entities;

namespace Marketing.Business.Repositories.Interfaces;

/// <summary>User queries that go beyond the generic repository.</summary>
public interface IUserRepository : IRepository<User>
{
    /// <summary>
    /// Loads a user by normalised email for the sign-in path, together with their tenant and roles.
    /// <para>
    /// This is the one query in the system that deliberately bypasses the tenant filter, because at
    /// sign-in there is no authenticated tenant yet - the tenant is a <em>result</em> of
    /// authentication, not an input to it. The lookup is by unique address and the caller must not
    /// disclose whether a match was found.
    /// </para>
    /// </summary>
    /// <param name="normalizedEmail">Lowercased address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<User?> FindForAuthenticationAsync(string normalizedEmail, CancellationToken cancellationToken = default);

    /// <summary>Loads a tracked user with their role assignments, for token issuance and refresh.</summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<User?> FindWithRolesAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>Returns whether an address is already registered anywhere on the platform.</summary>
    /// <param name="normalizedEmail">Lowercased address.</param>
    /// <param name="excludingUserId">User to exclude, when checking during an update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<bool> IsEmailTakenAsync(
        string normalizedEmail,
        long? excludingUserId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the role names granted to a user.</summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<string>> GetRoleNamesAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the active platform administrators.
    /// </summary>
    /// <remarks>
    /// Lives on the repository because it has to ignore the tenant filter: platform staff carry no
    /// tenant, so any query for them made while a tenant scope is entered matches nobody. That
    /// failure is silent — an empty list reads exactly like "there are none" — which is how a
    /// notification meant for a reviewer disappears without an error anywhere.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<User>> GetPlatformAdministratorsAsync(CancellationToken cancellationToken = default);
}
