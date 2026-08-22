using Marketing.Application.DTOs.BusinessDiscovery;

namespace Marketing.Application.Services.BusinessDiscovery;

/// <summary>
/// The categories the picker offers, and how each maps onto a provider's own vocabulary.
/// </summary>
/// <remarks>
/// The slug is the platform's, not the provider's. Providers disagree about what a category is
/// called and change their taxonomies between releases; storing a raw provider token in saved UI
/// state would mean a provider change silently breaks every saved search. The translation happens
/// here, at the boundary, and nowhere else.
/// </remarks>
public static class BusinessCategories
{
    /// <summary>Slug, label, group, and the Google Places type it corresponds to.</summary>
    private static readonly (string Id, string Label, string Group, string GoogleType)[] Catalogue =
    [
        ("barber", "Barber", "Personal care", "hair_care"),
        ("beauty_salon", "Beauty salon", "Personal care", "beauty_salon"),
        ("spa", "Spa", "Personal care", "spa"),
        ("gym", "Gym", "Personal care", "gym"),

        ("restaurant", "Restaurant", "Food & drink", "restaurant"),
        ("cafe", "Cafe", "Food & drink", "cafe"),
        ("bakery", "Bakery", "Food & drink", "bakery"),
        ("bar", "Bar", "Food & drink", "bar"),

        ("clothing_store", "Clothing store", "Retail", "clothing_store"),
        ("supermarket", "Supermarket", "Retail", "supermarket"),
        ("furniture_store", "Furniture store", "Retail", "furniture_store"),
        ("electronics_store", "Electronics store", "Retail", "electronics_store"),
        ("jewelry_store", "Jewellery store", "Retail", "jewelry_store"),
        ("florist", "Florist", "Retail", "florist"),
        ("pet_store", "Pet store", "Retail", "pet_store"),

        ("pharmacy", "Pharmacy", "Health", "pharmacy"),
        ("dentist", "Dentist", "Health", "dentist"),
        ("doctor", "Doctor", "Health", "doctor"),
        ("veterinary_care", "Veterinary clinic", "Health", "veterinary_care"),

        ("real_estate_agency", "Estate agency", "Professional services", "real_estate_agency"),
        ("insurance_agency", "Insurance agency", "Professional services", "insurance_agency"),
        ("accounting", "Accountant", "Professional services", "accounting"),
        ("lawyer", "Solicitor", "Professional services", "lawyer"),
        ("travel_agency", "Travel agency", "Professional services", "travel_agency"),

        ("car_repair", "Car repair", "Automotive", "car_repair"),
        ("car_dealer", "Car dealership", "Automotive", "car_dealer"),
        ("car_wash", "Car wash", "Automotive", "car_wash"),

        ("school", "School", "Education", "school"),
        ("university", "University", "Education", "university"),

        ("lodging", "Hotel", "Hospitality", "lodging"),
        ("event_venue", "Event venue", "Hospitality", "event_venue"),
    ];

    private static readonly Dictionary<string, string> SlugToGoogleType =
        Catalogue.ToDictionary(entry => entry.Id, entry => entry.GoogleType, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every category, for the picker.</summary>
    public static IReadOnlyList<BusinessCategoryResponse> All { get; } =
        [.. Catalogue.Select(entry => new BusinessCategoryResponse(entry.Id, entry.Label, entry.Group))];

    /// <summary>Whether a slug is one this platform recognises.</summary>
    /// <param name="categoryId">The slug to check.</param>
    public static bool IsKnown(string? categoryId) =>
        !string.IsNullOrWhiteSpace(categoryId) && SlugToGoogleType.ContainsKey(categoryId);

    /// <summary>Translates a slug into the provider's own token.</summary>
    /// <param name="categoryId">The slug.</param>
    /// <returns>The provider token, or null when the slug is unknown.</returns>
    public static string? ToProviderType(string? categoryId) =>
        categoryId is not null && SlugToGoogleType.TryGetValue(categoryId, out var type) ? type : null;
}
