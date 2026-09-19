namespace Marketing.Common.Constants;

/// <summary>The nearby-business search radius, in one place.</summary>
/// <remarks>
/// Shared by the search endpoint, the plan editor's validation and the Places provider, so the three
/// cannot drift apart. A plan may narrow this, never widen it.
/// </remarks>
public static class SearchRadius
{
    /// <summary>Widest radius the platform searches, in whole kilometres, whatever the plan says.</summary>
    public const int MaximumKm = 10;
}
