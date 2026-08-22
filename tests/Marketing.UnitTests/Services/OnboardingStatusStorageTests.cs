using AwesomeAssertions;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.UnitTests.Services;

/// <summary>
/// Pins how <c>users.onboarding_status</c> behaves for rows that predate the column.
/// </summary>
/// <remarks>
/// Adding a non-nullable enum-as-string column makes EF backfill existing rows with an empty
/// string, which is not a member of the enum - so every user created before the product tour has a
/// blank in that column. That reads oddly in the database and looks like data loss, which is worth
/// knowing before anyone goes hunting for a bug that is not there.
/// </remarks>
public sealed class OnboardingStatusStorageTests
{
    private static readonly EnumToStringConverter<OnboardingStatus> Converter = new();

    [Fact]
    public void An_empty_stored_status_reads_back_as_not_started()
    {
        // The converter parses leniently and falls back to the zero member rather than throwing.
        // That is the saving grace here: a blank column is untidy, but it is read as "not started",
        // which is exactly the right answer for a user who has never seen the tour. Nothing is
        // broken by the backfill - it only looks broken.
        Converter.ConvertFromProvider("").Should().Be(OnboardingStatus.NotStarted);
    }

    [Fact]
    public void The_intended_value_round_trips()
    {
        // What new rows store, and what the backfill should be corrected to so the column is
        // legible to anyone reading the table directly.
        Converter.ConvertFromProvider("NotStarted").Should().Be(OnboardingStatus.NotStarted);
        Converter.ConvertToProvider(OnboardingStatus.NotStarted).Should().Be("NotStarted");
    }

    [Theory]
    [InlineData(OnboardingStatus.NotStarted)]
    [InlineData(OnboardingStatus.InProgress)]
    [InlineData(OnboardingStatus.Completed)]
    [InlineData(OnboardingStatus.Skipped)]
    public void Every_status_survives_storage(OnboardingStatus status)
    {
        // The column is 16 characters; a longer member name would be truncated on write and then
        // fail to parse on read, which is the same outage by a different route.
        var stored = (string)Converter.ConvertToProvider(status)!;

        stored.Length.Should().BeLessThanOrEqualTo(16);
        Converter.ConvertFromProvider(stored).Should().Be(status);
    }
}
