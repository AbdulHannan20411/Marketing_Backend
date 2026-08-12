using System.ComponentModel.DataAnnotations;

namespace Marketing.Application.Configurations;

/// <summary>Contact-import settings, bound from the <c>Imports</c> configuration section.</summary>
public sealed class ImportOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Imports";

    /// <summary>
    /// Largest upload accepted, in bytes.
    /// <para>
    /// Enforced here as well as by the host's request limit. The host's limit protects the server;
    /// this one gives the operator a sentence they can act on instead of a truncated connection.
    /// </para>
    /// </summary>
    [Range(1024, 1024L * 1024L * 512L)]
    public long MaxFileSizeBytes { get; init; } = 25L * 1024L * 1024L;

    /// <summary>Most data rows accepted in one upload.</summary>
    [Range(1, 1_000_000)]
    public int MaxRows { get; init; } = 50_000;

    /// <summary>
    /// Rows written per transaction during a commit.
    /// <para>
    /// A whole 50,000-row import in one transaction holds locks for minutes and rolls the lot back
    /// on the last row. Chunking bounds both, at the cost of a partial import being visible — which
    /// the progress counter reports rather than hides.
    /// </para>
    /// </summary>
    [Range(50, 10_000)]
    public int CommitChunkSize { get; init; } = 500;

    /// <summary>Jobs one poll claims.</summary>
    [Range(1, 100)]
    public int PollBatchSize { get; init; } = 5;

    /// <summary>Attempts after which a job is dead-lettered rather than retried.</summary>
    [Range(1, 20)]
    public int MaxAttempts { get; init; } = 5;

    /// <summary>
    /// How long a claim is honoured before another worker may take the job.
    /// <para>
    /// Longer than the slowest realistic run. Too short and a healthy worker's job is stolen and
    /// done twice; the state machine makes that safe, but it still wastes the work.
    /// </para>
    /// </summary>
    [Range(1, 240)]
    public int LeaseMinutes { get; init; } = 30;
}
