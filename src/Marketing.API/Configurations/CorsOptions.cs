using System.ComponentModel.DataAnnotations;

namespace Marketing.API.Configurations;

/// <summary>Cross-origin settings, bound from the <c>Cors</c> configuration section.</summary>
public sealed class ApiCorsOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Cors";

    /// <summary>Name of the registered CORS policy.</summary>
    public const string PolicyName = "marketing-spa";

    /// <summary>
    /// Origins permitted to call the API.
    /// <para>
    /// An explicit list, never a wildcard. The Angular client sends an <c>Authorization</c> header,
    /// and a wildcard origin combined with credentials is exactly the configuration that lets any
    /// site on the internet issue authenticated requests on a signed-in user's behalf.
    /// </para>
    /// </summary>
    [MinLength(1)]
    public string[] AllowedOrigins { get; init; } = [];

    /// <summary>How long a browser may cache the preflight response.</summary>
    [Range(60, 86_400)]
    public int PreflightMaxAgeSeconds { get; init; } = 3_600;
}
