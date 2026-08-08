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

/// <summary>
/// Provider-specific name matching for the entities global search covers.
/// <para>
/// Here rather than in the search service for the same reason as contact search: the translation
/// to PostgreSQL <c>ILIKE</c> is an Entity Framework concern, and keeping it in this layer is what
/// lets the Application layer stay free of EF imports.
/// </para>
/// </summary>
public static class SearchQueryExtensions
{
    /// <summary>Matches a campaign name, case-insensitively.</summary>
    public static IQueryable<Campaign> WhereNameMatches(this IQueryable<Campaign> source, string term)
    {
        ArgumentNullException.ThrowIfNull(source);

        return source.Where(campaign => EF.Functions.ILike(campaign.Name, $"%{term}%"));
    }

    /// <summary>Matches a template name, case-insensitively.</summary>
    public static IQueryable<MessageTemplate> WhereNameMatches(this IQueryable<MessageTemplate> source, string term)
    {
        ArgumentNullException.ThrowIfNull(source);

        return source.Where(template => EF.Functions.ILike(template.Name, $"%{term}%"));
    }

    /// <summary>Matches a user's display name or email, case-insensitively.</summary>
    public static IQueryable<User> WhereNameMatches(this IQueryable<User> source, string term)
    {
        ArgumentNullException.ThrowIfNull(source);

        return source.Where(user =>
            EF.Functions.ILike(user.DisplayName, $"%{term}%")
            || EF.Functions.ILike(user.Email, $"%{term}%"));
    }
}

/// <summary>Provider-specific query fragments for groups and tags.</summary>
public static class CatalogQueryExtensions
{
    /// <summary>
    /// Matches a group name exactly but case-insensitively.
    /// </summary>
    /// <remarks>
    /// <c>ILIKE</c> with no wildcards rather than <c>ToLower()</c>: the analyzer rejects the
    /// latter, and <c>string.Equals(..., StringComparison)</c> - which it recommends instead - has
    /// no SQL translation at all. This matches the partial unique index the database enforces.
    /// </remarks>
    /// <param name="source">Query being composed.</param>
    /// <param name="name">Name to match.</param>
    public static IQueryable<ContactGroup> WhereNameMatches(this IQueryable<ContactGroup> source, string name)
    {
        ArgumentNullException.ThrowIfNull(source);

        return source.Where(group => EF.Functions.ILike(group.Name, name));
    }

    /// <summary>Matches a tag name exactly but case-insensitively.</summary>
    /// <param name="source">Query being composed.</param>
    /// <param name="name">Name to match.</param>
    public static IQueryable<ContactTag> WhereNameMatches(this IQueryable<ContactTag> source, string name)
    {
        ArgumentNullException.ThrowIfNull(source);

        return source.Where(tag => EF.Functions.ILike(tag.Name, name));
    }
}
