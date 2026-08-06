using Marketing.DataAccess.Entities;
using Microsoft.EntityFrameworkCore;

namespace Marketing.Business.Extensions;

/// <summary>
/// Provider-specific query fragments for contacts.
/// <para>
/// These live here rather than in the service because they depend on Npgsql's <c>ILike</c>
/// translation. Keeping them in the persistence layer is what lets the Application layer compose
/// queries without importing Entity Framework.
/// </para>
/// </summary>
public static class ContactQueryExtensions
{
    /// <summary>
    /// Matches name, phone number and email, case-insensitively.
    /// <para>
    /// Translates to PostgreSQL <c>ILIKE</c>, so the match happens in the database. Doing it in
    /// memory would mean pulling every contact in the tenant back to filter a handful.
    /// </para>
    /// </summary>
    /// <param name="source">Query being composed.</param>
    /// <param name="search">Search term. A blank term applies no filter.</param>
    public static IQueryable<Contact> WhereMatchesSearch(this IQueryable<Contact> source, string? search)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (string.IsNullOrWhiteSpace(search))
        {
            return source;
        }

        // The term is only ever a bound parameter, never concatenated into SQL. The wildcards are
        // ours; any '%' the caller types is matched literally by ILIKE's own escaping rules.
        var term = $"%{search.Trim()}%";

        return source.Where(contact =>
            EF.Functions.ILike(contact.FullName, term)
            || EF.Functions.ILike(contact.PhoneNumber, term)
            || (contact.Email != null && EF.Functions.ILike(contact.Email, term)));
    }
}
