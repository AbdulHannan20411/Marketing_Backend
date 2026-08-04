namespace Marketing.Common.Constants;

/// <summary>
/// Well-known identities used when no interactive user is present.
/// <para>
/// Background jobs, migrations and seeding still have to satisfy the non-nullable
/// <c>CreatedBy</c> audit column. Using a fixed, reserved identifier makes those rows obvious in
/// an audit query instead of leaving them indistinguishable from a real user's changes.
/// </para>
/// </summary>
public static class SystemIdentity
{
    /// <summary>Identifier stamped on rows written by the platform itself.</summary>
    public static readonly Guid UserId = new("00000000-0000-0000-0000-000000000001");

    /// <summary>Display name shown for system-authored changes.</summary>
    public const string DisplayName = "System";

    /// <summary>Email used for the system principal. Never receives mail and cannot sign in.</summary>
    public const string Email = "system@platform.internal";
}
