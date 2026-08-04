using Marketing.Shared.Abstractions;

namespace Marketing.Infrastructure.Time;

/// <summary>Wall-clock implementation of <see cref="IDateTimeProvider"/>.</summary>
public sealed class SystemDateTimeProvider : IDateTimeProvider
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public DateTime UtcNowDateTime => DateTime.UtcNow;
}
