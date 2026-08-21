using static Marketing.Common.Constants.ContractEnums;

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


/// <summary>
/// Request to change the signed-in user's own name, address or password.
/// </summary>
/// <remarks>
/// Every field is optional and absent means "leave alone", never "clear", so a caller can change one
/// thing without restating the rest.
/// <para>
/// All three are applied in one transaction. Splitting them across two calls creates an ordering
/// trap: the address change is confirmed with the current password, so changing the password first
/// makes that confirmation stale and the combined change fails every time. One transaction has one
/// outcome and no partial success to explain to the user.
/// </para>
/// </remarks>
/// <param name="DisplayName">New display name, or null to leave it.</param>
/// <param name="Email">New address, or null to leave it.</param>
/// <param name="NewPassword">New password, or null to leave it.</param>
/// <param name="CurrentPassword">
/// The existing password. Required whenever <paramref name="Email"/> or
/// <paramref name="NewPassword"/> is present, and ignored otherwise.
/// </param>
public sealed record UpdateProfileRequest(
    string? DisplayName = null,
    string? Email = null,
    string? NewPassword = null,
    string? CurrentPassword = null)
{
    /// <summary>Whether this request touches anything a stolen session must not be able to touch.</summary>
    public bool RequiresPassword =>
        !string.IsNullOrWhiteSpace(Email) || !string.IsNullOrWhiteSpace(NewPassword);
}


/// <summary>A user's progress through the product tour.</summary>
/// <param name="Status">How far they have got.</param>
/// <param name="StepIndex">Zero-based position an interrupted run reached.</param>
/// <param name="UpdatedAt">Instant the state last changed, or null if it never has.</param>
public sealed record OnboardingStateResponse(
    OnboardingStatus Status,
    int StepIndex,
    DateTimeOffset? UpdatedAt);

/// <summary>Request to record a user's progress through the product tour.</summary>
/// <remarks>
/// <c>updatedAt</c> is deliberately absent. The client sends one and it is ignored: a timestamp a
/// caller controls is not evidence of when anything happened, and the server has a clock.
/// </remarks>
/// <param name="Status">How far they have got.</param>
/// <param name="StepIndex">Zero-based position. Negative values are treated as zero.</param>
public sealed record UpdateOnboardingStateRequest(
    OnboardingStatus Status,
    int StepIndex = 0);
