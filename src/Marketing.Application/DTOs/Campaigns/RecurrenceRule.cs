using System.Text.Json;
using System.Text.Json.Serialization;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.DTOs.Campaigns;

/// <summary>
/// How a campaign repeats.
/// <para>
/// A flat object rather than an RRULE string, deliberately. The client renders this as a form and
/// validates it field by field; a grammar cannot be validated that way, and a reviewer reading an
/// audit entry cannot read one either.
/// </para>
/// <para>
/// <b>The local date, time and zone are stored as given and never normalised to UTC.</b> "Every
/// Monday at 9am in Europe/London" is 09:00 GMT in winter and 09:00 BST in summer. Normalising on
/// write freezes one of those and the campaign drifts an hour twice a year; the conversion belongs
/// at each firing instead.
/// </para>
/// </summary>
/// <param name="Frequency">How often it repeats.</param>
/// <param name="Interval">Every N periods. At least 1. Ignored when <see cref="Frequency"/> is once.</param>
/// <param name="Weekdays">0=Sunday to 6=Saturday. Weekly only, at least one.</param>
/// <param name="MonthlyMode">Whether a monthly rule picks a date or an ordinal weekday.</param>
/// <param name="DayOfMonth">1-31, for dayOfMonth mode and for yearly.</param>
/// <param name="Ordinal">Which occurrence of <see cref="OrdinalWeekday"/> within the month.</param>
/// <param name="OrdinalWeekday">0=Sunday to 6=Saturday, paired with <see cref="Ordinal"/>.</param>
/// <param name="Month">1-12. Yearly only.</param>
/// <param name="StartDate">First eligible date, in <see cref="TimeZone"/> and not UTC.</param>
/// <param name="Time">Local time of day, in <see cref="TimeZone"/> and not UTC.</param>
/// <param name="TimeZone">IANA zone name, for example <c>Europe/London</c>. Never an offset.</param>
/// <param name="EndCondition">What stops it.</param>
/// <param name="EndDate">Last eligible date, when <see cref="EndCondition"/> is onDate.</param>
/// <param name="OccurrenceCount">Number of firings, when <see cref="EndCondition"/> is afterCount.</param>
public sealed record RecurrenceRule(
    RecurrenceFrequency Frequency,
    int Interval,
    IReadOnlyList<int>? Weekdays,
    MonthlyMode? MonthlyMode,
    int? DayOfMonth,
    MonthlyOrdinal? Ordinal,
    int? OrdinalWeekday,
    int? Month,
    DateOnly StartDate,
    [property: JsonConverter(typeof(HourMinuteTimeConverter))] TimeOnly Time,
    string TimeZone,
    RecurrenceEndCondition EndCondition,
    DateOnly? EndDate,
    int? OccurrenceCount)
{
    /// <summary>True when this rule describes a single dispatch.</summary>
    [JsonIgnore]
    public bool IsSingleOccurrence => Frequency == RecurrenceFrequency.Once;
}

/// <summary>
/// Reads and writes <see cref="TimeOnly"/> as <c>HH:mm</c>.
/// <para>
/// The client sends and expects <c>"09:00"</c>. The built-in converter demands seconds on read and
/// emits them on write, so without this the round trip fails in one direction and changes the
/// string in the other.
/// </para>
/// </summary>
public sealed class HourMinuteTimeConverter : JsonConverter<TimeOnly>
{
    private const string Format = "HH:mm";

    /// <inheritdoc />
    public override TimeOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();

        // Seconds are accepted on read even though they are never sent. A client that grows a
        // seconds field should not be met with a parse failure it cannot interpret.
        if (TimeOnly.TryParseExact(text, "HH:mm", out var minutes))
        {
            return minutes;
        }

        if (TimeOnly.TryParseExact(text, "HH:mm:ss", out var seconds))
        {
            return seconds;
        }

        throw new JsonException($"Expected a time of day as HH:mm, but found \"{text}\".");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TimeOnly value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(value.ToString(Format, System.Globalization.CultureInfo.InvariantCulture));
    }
}
