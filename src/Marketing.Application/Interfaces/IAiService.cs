namespace Marketing.Application.Interfaces;

/// <summary>
/// Generates text from a prompt with a large language model.
/// </summary>
/// <remarks>
/// A seam, like <see cref="IPlaceProvider"/>. Which provider answers - Gemini today, possibly OpenAI,
/// Azure OpenAI or a local Ollama later - is a configuration and commercial decision, so nothing
/// above this interface, and nothing in the browser, knows which one it is.
/// <para>
/// <b>Nothing here is reachable from the browser.</b> The provider key lives in server configuration
/// only; a key shipped to the client is a key published to every customer.
/// </para>
/// <para>
/// Implementations translate every provider failure into a platform exception and never let the
/// provider's own error text, or the key, reach the caller.
/// </para>
/// </remarks>
public interface IAiService
{
    /// <summary>Whether the provider is configured and usable.</summary>
    public bool IsConfigured { get; }

    /// <summary>Generates a text answer for a prompt.</summary>
    /// <param name="prompt">What the user asked for. Already validated for presence and length.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The generated text, never empty.</returns>
    public Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken = default);
}
