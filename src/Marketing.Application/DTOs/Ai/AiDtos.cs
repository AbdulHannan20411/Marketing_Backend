namespace Marketing.Application.DTOs.Ai;

/// <summary>A request to generate text.</summary>
/// <param name="Prompt">What the user wants created. Required, at most <see cref="MaxPromptLength"/> characters.</param>
public sealed record AiGenerateRequest(string Prompt)
{
    /// <summary>
    /// Longest prompt accepted. Generous for a marketing brief, and a ceiling on how much provider
    /// quota a single request can spend.
    /// </summary>
    public const int MaxPromptLength = 4000;
}

/// <summary>The generated answer.</summary>
/// <param name="Answer">Generated text. Plain text; the client renders it as text, never as HTML.</param>
public sealed record AiGenerateResponse(string Answer);
