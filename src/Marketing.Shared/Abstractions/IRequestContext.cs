namespace Marketing.Shared.Abstractions;

/// <summary>
/// Ambient metadata about the operation in flight.
/// <para>
/// Implemented over <c>HttpContext</c> for requests and over the job execution context for Quartz,
/// so audit rows and log entries carry the same correlation fields whichever entry point produced
/// them.
/// </para>
/// </summary>
public interface IRequestContext
{
    /// <summary>Identifier tying every log entry and audit row for this operation together.</summary>
    public string CorrelationId { get; }

    /// <summary>Client address, or null when there is no remote caller.</summary>
    public string? IpAddress { get; }

    /// <summary>Client user agent, or null when there is no remote caller.</summary>
    public string? UserAgent { get; }
}
