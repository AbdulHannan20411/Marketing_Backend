using Marketing.Common.Exceptions;

namespace Marketing.Application.Services;

/// <summary>Field rules for groups and tags, kept beside the contact rules for the same reason.</summary>
internal static class CatalogRules
{
    /// <summary>Longest accepted group name.</summary>
    public const int MaxGroupNameLength = 60;

    /// <summary>Longest accepted tag name.</summary>
    public const int MaxTagNameLength = 40;

    /// <summary>Longest accepted group description.</summary>
    public const int MaxDescriptionLength = 200;

    /// <summary>Validates and trims a group name.</summary>
    /// <param name="name">Name as supplied.</param>
    /// <exception cref="ValidationException">Empty, or too long.</exception>
    public static string NormaliseGroupName(string? name) =>
        Normalise(name, MaxGroupNameLength, "name", "group");

    /// <summary>Validates and trims a tag name.</summary>
    /// <param name="name">Name as supplied.</param>
    /// <exception cref="ValidationException">Empty, or too long.</exception>
    public static string NormaliseTagName(string? name) =>
        Normalise(name, MaxTagNameLength, "name", "tag");

    /// <summary>
    /// Trims a description, defaulting absence to an empty string.
    /// </summary>
    /// <remarks>
    /// Never null. The client renders it directly, and a null would print "null" on the card.
    /// </remarks>
    /// <param name="description">Description as supplied.</param>
    /// <exception cref="ValidationException">Too long.</exception>
    public static string NormaliseDescription(string? description)
    {
        var trimmed = description?.Trim() ?? string.Empty;

        return trimmed.Length > MaxDescriptionLength
            ? throw new ValidationException(
                "description",
                $"Description must be {MaxDescriptionLength} characters or fewer.")
            : trimmed;
    }

    private static string Normalise(string? value, int maxLength, string field, string subject)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ValidationException(field, $"Enter a {subject} name.");
        }

        return trimmed.Length > maxLength
            ? throw new ValidationException(
                field,
                $"A {subject} name must be {maxLength} characters or fewer.")
            : trimmed;
    }
}
