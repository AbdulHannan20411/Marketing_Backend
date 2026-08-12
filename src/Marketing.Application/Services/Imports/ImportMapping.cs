using System.Text.Json;
using Marketing.Application.DTOs.Imports;
using Marketing.DataAccess.Entities;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.Imports;

/// <summary>
/// Translations between stored import state and the shapes the client is promised.
/// <para>
/// Gathered in one place because both the read side and the workers need them, and because the
/// domain and the contract deliberately do not line up: the batch has two waiting states where the
/// contract has one, and the mapping is stored as JSON rather than as columns.
/// </para>
/// </summary>
internal static class ImportMapping
{
    /// <summary>Header names that suggest each contact field, most specific first.</summary>
    private static readonly (string Field, string[] Candidates)[] Suggestions =
    [
        (nameof(ImportColumnMap.FullName), ["full name", "fullname", "name", "contact", "customer"]),
        (nameof(ImportColumnMap.PhoneNumber), ["phone number", "phonenumber", "phone", "mobile", "msisdn", "number"]),
        (nameof(ImportColumnMap.Email), ["email address", "e-mail", "email"]),
        (nameof(ImportColumnMap.Country), ["country code", "country", "iso"]),
        (nameof(ImportColumnMap.Status), ["status", "consent", "subscription"]),
        (nameof(ImportColumnMap.Tags), ["tags", "tag", "labels"]),
        (nameof(ImportColumnMap.Groups), ["groups", "group", "segments", "lists"]),
    ];

    private static readonly JsonSerializerOptions MappingJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Projects a stored status onto the contract's.
    /// </summary>
    /// <remarks>
    /// The two waiting states collapse into one. Internally a batch that has been mapped but not
    /// confirmed is distinct from one that has not been mapped at all — the workers need the
    /// difference — but the client draws the same screen for both and was specified one value.
    /// </remarks>
    /// <param name="status">Stored status.</param>
    public static BatchStatus ToContract(ContactImportStatus status) => status switch
    {
        ContactImportStatus.Queued => BatchStatus.Pending,
        ContactImportStatus.Processing => BatchStatus.Processing,
        ContactImportStatus.AwaitingMapping or ContactImportStatus.AwaitingConfirmation =>
            BatchStatus.AwaitingMapping,
        ContactImportStatus.Committing => BatchStatus.Committing,
        ContactImportStatus.Completed => BatchStatus.Completed,
        ContactImportStatus.CompletedWithErrors => BatchStatus.CompletedWithErrors,
        ContactImportStatus.Cancelled => BatchStatus.Cancelled,
        _ => BatchStatus.Failed,
    };

    /// <summary>Reads a stored column mapping, returning null when none was saved.</summary>
    /// <param name="json">Stored JSON, or null.</param>
    public static ImportColumnMap? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ImportColumnMap>(json, MappingJson);
        }
        catch (JsonException)
        {
            // A mapping written by an older shape is treated as absent rather than failing the
            // whole screen: the client falls back to the suggestion, which is recoverable.
            return null;
        }
    }

    /// <summary>Serialises a column mapping for storage.</summary>
    /// <param name="map">Mapping to store.</param>
    public static string ToJson(ImportColumnMap map) => JsonSerializer.Serialize(map, MappingJson);

    /// <summary>
    /// Guesses which column feeds which field from the header names.
    /// </summary>
    /// <remarks>
    /// A convenience, never a decision: the operator confirms or changes it before committing. It
    /// exists because the common case is a well-labelled export, and making someone map seven
    /// obvious columns by hand every time is friction for no benefit.
    /// </remarks>
    /// <param name="columns">Headers found in the file.</param>
    public static ImportColumnMap Suggest(IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var chosen = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (field, candidates) in Suggestions)
        {
            // Exact matches first, across every field, before any field settles for a partial one.
            // Otherwise a file with both "Name" and "Country Name" can see the second field claim
            // the column the first one needed.
            var match = columns.FirstOrDefault(column =>
                !taken.Contains(column)
                && candidates.Any(candidate =>
                    string.Equals(column.Trim(), candidate, StringComparison.OrdinalIgnoreCase)));

            if (match is null)
            {
                continue;
            }

            taken.Add(match);
            chosen[field] = match;
        }

        return new ImportColumnMap(
            Value(nameof(ImportColumnMap.FullName)),
            Value(nameof(ImportColumnMap.PhoneNumber)),
            Value(nameof(ImportColumnMap.Email)),
            Value(nameof(ImportColumnMap.Country)),
            Value(nameof(ImportColumnMap.Status)),
            Value(nameof(ImportColumnMap.Tags)),
            Value(nameof(ImportColumnMap.Groups)));

        string? Value(string field) => chosen.GetValueOrDefault(field);
    }

    /// <summary>Counters for a batch, in the shape the client renders.</summary>
    /// <param name="batch">Batch to describe.</param>
    public static ImportStatistics Statistics(ContactImportBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        return new ImportStatistics(
            batch.TotalRows,
            batch.ImportedCount,
            batch.UpdatedCount,
            batch.DuplicateRows,
            batch.FailedCount,
            batch.SkippedCount);
    }

    /// <summary>
    /// How far through the current stage the batch is, as a whole percentage.
    /// </summary>
    /// <remarks>
    /// Zero while queued and one hundred once finished, whatever the counters say — a batch that
    /// failed at row three is finished, and a bar frozen at 6% is a worse answer than a bar at the
    /// end next to a failure.
    /// </remarks>
    /// <param name="batch">Batch to measure.</param>
    public static int ProgressPercent(ContactImportBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (ImportStateMachine.IsTerminal(batch.Status))
        {
            return 100;
        }

        // Waiting on the operator, not on us: the stage that was running has finished, and a bar
        // stuck at 99% next to "choose your columns" reads as a job that stalled.
        if (batch.Status is ContactImportStatus.AwaitingMapping or ContactImportStatus.AwaitingConfirmation)
        {
            return 100;
        }

        if (batch.Status == ContactImportStatus.Queued || batch.TotalRows <= 0)
        {
            return 0;
        }

        var done = Math.Clamp(batch.ProcessedRows, 0, batch.TotalRows);

        // Capped below the end while work remains, so the client never shows a completed bar for a
        // run that is still going.
        return Math.Min(99, (int)(done * 100L / batch.TotalRows));
    }

    /// <summary>Default wording for a failure code, used when nothing more specific was recorded.</summary>
    /// <param name="code">The failure.</param>
    public static string Describe(ImportErrorCode code) => code switch
    {
        ImportErrorCode.InvalidPhoneNumber => "The phone number is missing or could not be read.",
        ImportErrorCode.MissingRequiredField => "A required field was empty.",
        ImportErrorCode.InvalidEmail => "The email address is not valid.",
        ImportErrorCode.DuplicateContact => "A contact with this number already exists.",
        ImportErrorCode.DuplicateInFile => "This number appears earlier in the same file.",
        ImportErrorCode.UnsupportedColumn => "That column is not one this import understands.",
        ImportErrorCode.InvalidCountry => "The country could not be recognised.",
        ImportErrorCode.DatabaseError => "The row could not be saved.",
        ImportErrorCode.PlanLimitExceeded => "Your plan's contact limit was reached before this row.",
        _ => "The row could not be used.",
    };

    /// <summary>Pairs a row's cells with the file's headers.</summary>
    /// <param name="columns">Headers, in file order.</param>
    /// <param name="values">Cells, positionally aligned with the headers.</param>
    public static Dictionary<string, string> Keyed(IReadOnlyList<string> columns, IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(values);

        var keyed = new Dictionary<string, string>(columns.Count, StringComparer.Ordinal);

        for (var index = 0; index < columns.Count; index++)
        {
            keyed[columns[index]] = index < values.Count ? values[index] : string.Empty;
        }

        return keyed;
    }
}
