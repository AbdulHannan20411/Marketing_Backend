namespace Marketing.Application.Contracts;

/// <summary>
/// Tells a worker that an export is waiting.
/// </summary>
/// <remarks>
/// <b>Two numbers, and deliberately nothing else.</b> Everything about the export - the dataset,
/// the filters, the columns, who asked - is on the job row, which the worker reads. Putting any
/// of it here would mean a message that can disagree with the database, and a message big enough
/// to be worth worrying about when a queue backs up.
/// <para>
/// <c>TenantId</c> is here because the worker has no request to resolve one from: it enters that
/// workspace explicitly before it reads anything, so the ordinary tenant filters apply to it
/// exactly as they would to a request. It is checked against the job row on arrival rather than
/// trusted - a message is not proof of anything.
/// </para>
/// </remarks>
/// <param name="ExportJobId">Key of the <c>ExportJob</c> row to run.</param>
/// <param name="TenantId">Workspace the job belongs to.</param>
public sealed record ExportJobQueued(long ExportJobId, long TenantId);
