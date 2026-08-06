namespace Marketing.Shared.Abstractions;

/// <summary>
/// Supplies the current time. Injected rather than calling <see cref="DateTimeOffset.UtcNow"/>
/// directly so that token expiry, campaign scheduling and audit stamps are deterministic in tests.
/// </summary>
public interface IDateTimeProvider
{
    /// <summary>Current instant in UTC.</summary>
    public DateTimeOffset UtcNow { get; }

    /// <summary>Current UTC instant as a <see cref="DateTime"/> with <see cref="DateTimeKind.Utc"/>.</summary>
    public DateTime UtcNowDateTime { get; }
}
