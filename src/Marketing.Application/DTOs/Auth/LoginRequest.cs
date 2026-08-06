namespace Marketing.Application.DTOs.Auth;

/// <summary>Credentials presented at sign-in.</summary>
/// <param name="Email">Email address. Normalised server-side before lookup.</param>
/// <param name="Password">Plaintext password. Never logged, never persisted.</param>
/// <param name="RememberMe">
/// Whether the client intends a long-lived session. Advisory: refresh-token lifetime is decided by
/// server policy, not by the caller.
/// </param>
/// <param name="Portal">
/// Which sign-in entrance was used - <see cref="LoginPortals.Admin"/> or
/// <see cref="LoginPortals.SuperAdmin"/>.
/// <para>
/// Optional so the existing client keeps working, but supply it. The front end already refuses an
/// account that does not match the entrance it was used at, and that check is client-side only,
/// which makes it a UX boundary rather than a security one. When this is present the server
/// enforces it and the boundary becomes real.
/// </para>
/// </param>
public sealed record LoginRequest(
    string Email,
    string Password,
    bool RememberMe = false,
    string? Portal = null);

/// <summary>Accepted values for <see cref="LoginRequest.Portal"/>.</summary>
public static class LoginPortals
{
    /// <summary>The tenant-facing entrance at <c>/auth/login</c>.</summary>
    public const string Admin = "admin";

    /// <summary>The platform entrance at <c>/superadmin/login</c>.</summary>
    public const string SuperAdmin = "superadmin";

    /// <summary>Every accepted value.</summary>
    public static readonly IReadOnlyList<string> All = [Admin, SuperAdmin];
}
