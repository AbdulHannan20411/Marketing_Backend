namespace Marketing.Application.DTOs.Auth;

/// <summary>Request to activate an invited account.</summary>
/// <param name="Token">Single-use token from the invitation link.</param>
/// <param name="Password">The password the user chooses.</param>
public sealed record AcceptInvitationRequest(string Token, string Password);

/// <summary>Request to set a new password using a reset link.</summary>
/// <param name="Token">Single-use token from the reset link.</param>
/// <param name="Password">The new password.</param>
public sealed record ResetPasswordRequest(string Token, string Password);

/// <summary>Request to change the signed-in user's own password.</summary>
/// <param name="CurrentPassword">The existing password, re-entered to prove presence.</param>
/// <param name="NewPassword">The new password.</param>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
