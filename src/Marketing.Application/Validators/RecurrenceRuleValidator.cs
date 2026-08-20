using FluentValidation;
using Marketing.Application.DTOs.Campaigns;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Validators;

/// <summary>
/// Validates <see cref="RecurrenceRule"/>.
/// <para>
/// The client validates the same rules in the form, so nothing here should be the first time an
/// operator hears about a problem. It is repeated because the client is not the only caller and a
/// rule that survives to the dispatcher produces a campaign that silently never fires.
/// </para>
/// </summary>
public sealed class RecurrenceRuleValidator : AbstractValidator<RecurrenceRule>
{
    /// <summary>Initialises a new instance.</summary>
    public RecurrenceRuleValidator()
    {
        RuleFor(rule => rule.Frequency).IsInEnum().WithMessage("Choose how often the campaign repeats.");

        RuleFor(rule => rule.TimeZone)
            .NotEmpty().WithMessage("Choose a timezone.")
            .Must(BeAKnownTimeZone)
            .WithMessage("That timezone is not recognised. Use an IANA name such as Europe/London.");

        // A one-off ignores everything below, so none of it is worth refusing over.
        When(rule => !rule.IsSingleOccurrence, () =>
        {
            RuleFor(rule => rule.Interval)
                .GreaterThanOrEqualTo(1).WithMessage("Repeat at least every one period.")
                .LessThanOrEqualTo(999).WithMessage("That interval is too large to be deliberate.");

            RuleFor(rule => rule.EndCondition).IsInEnum().WithMessage("Choose when the campaign should stop.");

            When(rule => rule.EndCondition == RecurrenceEndCondition.OnDate, () =>
            {
                RuleFor(rule => rule.EndDate)
                    .NotNull().WithMessage("Choose the date the campaign should stop.");

                RuleFor(rule => rule.EndDate)
                    .GreaterThanOrEqualTo(rule => rule.StartDate)
                    .When(rule => rule.EndDate is not null)
                    .WithMessage("The end date cannot be before the start date.");
            });

            RuleFor(rule => rule.OccurrenceCount)
                .NotNull().WithMessage("Choose how many times the campaign should run.")
                .GreaterThanOrEqualTo(1).WithMessage("Run the campaign at least once.")
                .When(rule => rule.EndCondition == RecurrenceEndCondition.AfterCount);
        });

        When(rule => rule.Frequency == RecurrenceFrequency.Weekly, () =>
        {
            RuleFor(rule => rule.Weekdays)
                .NotNull().WithMessage("Choose at least one day of the week.")
                .Must(days => days is { Count: > 0 }).WithMessage("Choose at least one day of the week.")
                .Must(days => days is null || days.All(day => day is >= 0 and <= 6))
                .WithMessage("Days of the week run from 0 (Sunday) to 6 (Saturday).");
        });

        When(rule => rule.Frequency is RecurrenceFrequency.Monthly or RecurrenceFrequency.Yearly, () =>
        {
            RuleFor(rule => rule.MonthlyMode).NotNull().IsInEnum()
                .WithMessage("Choose whether to repeat on a date or on a weekday.");

            When(rule => rule.MonthlyMode == Common.Constants.ContractEnums.MonthlyMode.DayOfMonth, () =>
            {
                RuleFor(rule => rule.DayOfMonth)
                    .NotNull().WithMessage("Choose a day of the month.")
                    .InclusiveBetween(1, 31).WithMessage("Days of the month run from 1 to 31.");
            });

            When(rule => rule.MonthlyMode == Common.Constants.ContractEnums.MonthlyMode.DayOfWeek, () =>
            {
                RuleFor(rule => rule.Ordinal).NotNull().IsInEnum()
                    .WithMessage("Choose which occurrence of the weekday.");

                RuleFor(rule => rule.OrdinalWeekday)
                    .NotNull().WithMessage("Choose a weekday.")
                    .InclusiveBetween(0, 6).WithMessage("Weekdays run from 0 (Sunday) to 6 (Saturday).");
            });
        });

        When(rule => rule.Frequency == RecurrenceFrequency.Yearly, () =>
        {
            RuleFor(rule => rule.Month)
                .NotNull().WithMessage("Choose a month.")
                .InclusiveBetween(1, 12).WithMessage("Months run from 1 to 12.");
        });
    }

    /// <summary>
    /// True when the runtime can resolve the zone.
    /// </summary>
    /// <remarks>
    /// .NET 6 and later accept IANA identifiers on Windows as well as Linux, so no conversion table
    /// is needed. The lookup is the only honest test - a name that cannot be resolved here is one
    /// the dispatcher could not resolve either, and the campaign would never fire.
    /// </remarks>
    private static bool BeAKnownTimeZone(string? timeZone)
    {
        if (string.IsNullOrWhiteSpace(timeZone))
        {
            return false;
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _);
    }
}
