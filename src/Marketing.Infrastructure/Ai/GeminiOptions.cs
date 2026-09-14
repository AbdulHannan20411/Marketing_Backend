using System.ComponentModel.DataAnnotations;

namespace Marketing.Infrastructure.Ai;

/// <summary>Gemini settings, bound from the <c>Gemini</c> section.</summary>
/// <remarks>
/// <para>
/// <b>The key belongs in a secret store, never in <c>appsettings.json</c>.</b> Supply it as
/// <c>Gemini:ApiKey</c> through <c>dotnet user-secrets</c> locally, or as the environment variable
/// <c>Gemini__ApiKey</c> or <c>GEMINI_API_KEY</c> elsewhere.
/// </para>
/// <para>
/// No <c>[Required]</c> on the key, matching <c>PlacesOptions</c>: an unset key means "this deployment
/// does not offer the AI assistant", which the endpoint reports cleanly rather than stopping the whole
/// application from starting.
/// </para>
/// </remarks>
public sealed class GeminiOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Gemini";

    /// <summary>Environment variable read when <see cref="ApiKey"/> is not otherwise configured.</summary>
    public const string ApiKeyEnvironmentVariable = "GEMINI_API_KEY";

    /// <summary>Provider API key. Empty disables the assistant. Never logged.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Model id, for example <c>gemini-3.5-flash-lite</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Model { get; set; } = "gemini-3.5-flash-lite";

    /// <summary>How long to wait for the provider before giving up.</summary>
    [Range(5, 120)]
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Ceiling on generated tokens. Thinking models count their reasoning against this too, so it is
    /// set well above the length of a marketing message.
    /// </summary>
    [Range(256, 8192)]
    public int MaxOutputTokens { get; set; } = 2048;
}
