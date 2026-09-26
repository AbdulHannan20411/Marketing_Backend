using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Constants;
using Microsoft.EntityFrameworkCore;

namespace Marketing.Application.Services.Audit;

/// <summary>Puts names to the user ids stored in audit columns.</summary>
public interface IActorNames
{
    /// <summary>
    /// Resolves display names for a set of user ids, in one query.
    /// </summary>
    /// <remarks>
    /// Ids that name nobody are simply absent from the result, so a caller renders whatever it
    /// shows for "unknown" rather than having to handle an exception for a row that outlived the
    /// person who wrote it.
    /// </remarks>
    /// <param name="userIds">Ids to resolve. Duplicates and zeroes are tolerated.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyDictionary<long, string>> ResolveAsync(
        IEnumerable<long?> userIds,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IActorNames" />
public sealed class ActorNames : IActorNames
{
    private readonly IUserRepository _users;
    private readonly IQueryExecutor _queries;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="users">User repository.</param>
    /// <param name="queries">Query executor.</param>
    public ActorNames(IUserRepository users, IQueryExecutor queries)
    {
        _users = users;
        _queries = queries;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, string>> ResolveAsync(
        IEnumerable<long?> userIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        var wanted = userIds
            .Where(id => id is > 0 && id != AppConstants.Platform.SystemUserId)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        if (wanted.Count == 0)
        {
            return new Dictionary<long, string>();
        }

        var rows = await _queries.ToListAsync(
            _users.Query()

                // Soft-deleted users included, on purpose: a column that reads "—" the day
                // somebody leaves the company is worse than no column, and their name is already
                // written on every row they touched.
                .IgnoreQueryFilters()
                .Where(user => wanted.Contains(user.Id))
                .Select(user => new { user.Id, user.DisplayName }),
            cancellationToken);

        return rows.ToDictionary(row => row.Id, row => row.DisplayName);
    }
}
