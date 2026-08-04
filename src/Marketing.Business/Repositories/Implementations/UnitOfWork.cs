using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Context;
using Microsoft.EntityFrameworkCore;

namespace Marketing.Business.Repositories.Implementations;

/// <summary>Entity Framework implementation of <see cref="IUnitOfWork"/>.</summary>
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly ApplicationDbContext _context;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="context">Database context.</param>
    public UnitOfWork(ApplicationDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _context.SaveChangesAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // Retries are enabled on the Npgsql provider, so a transaction has to be driven by the
        // execution strategy. Opening one directly would throw at runtime the first time a
        // transient fault occurred - the strategy cannot resume a partially applied transaction,
        // it has to replay the whole delegate.
        var strategy = _context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(
            cancellationToken,
            async (token) =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(token);

                var result = await operation(token);

                await _context.SaveChangesAsync(token);
                await transaction.CommitAsync(token);

                return result;
            });
    }

    /// <inheritdoc />
    public Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return ExecuteInTransactionAsync<object?>(
            async token =>
            {
                await operation(token);
                return null;
            },
            cancellationToken);
    }
}
