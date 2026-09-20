using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Marketing.Business.Extensions;

/// <summary>
/// Tells one kind of write failure from another.
/// </summary>
/// <remarks>
/// Here rather than in a service for the same reason the query fragments are: the error codes are
/// PostgreSQL's, and reading them is a persistence concern. A service that catches every
/// <see cref="DbUpdateException"/> alike will sooner or later report a real defect as the benign
/// case it was expecting - which is exactly how a foreign key violation came to be logged as a
/// duplicate message for a fortnight.
/// </remarks>
public static class DatabaseErrors
{
    /// <summary>Whether a write failed because a unique index already held that value.</summary>
    /// <param name="exception">Failure reported by the save.</param>
    public static bool IsUniqueViolation(this DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception.InnerException is PostgresException postgres
               && string.Equals(postgres.SqlState, PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal);
    }
}
