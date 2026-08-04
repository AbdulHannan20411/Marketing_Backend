namespace Marketing.Application.DTOs.Auth;

/// <summary>Credentials presented at sign-in.</summary>
/// <param name="Email">Email address. Normalised server-side before lookup.</param>
/// <param name="Password">Plaintext password. Never logged, never persisted.</param>
public sealed record LoginRequest(string Email, string Password);
