namespace Marketing.Common.Enums;

/// <summary>Lifecycle state of a user account.</summary>
public enum UserStatus
{
    /// <summary>Invited but has not accepted the invitation; sign-in is blocked.</summary>
    Invited = 0,

    /// <summary>Normal, sign-in permitted.</summary>
    Active = 1,

    /// <summary>Disabled by an administrator; sign-in is blocked and sessions are revoked.</summary>
    Disabled = 2,

    /// <summary>Locked out after repeated failed sign-in attempts; clears automatically.</summary>
    Locked = 3,
}
