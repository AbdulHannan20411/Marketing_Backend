using FluentValidation;
using Marketing.Application.DTOs.WhatsApp;

namespace Marketing.Application.Validators;

/// <summary>Validates <see cref="ConnectWhatsAppRequest"/>.</summary>
public sealed class ConnectWhatsAppRequestValidator : AbstractValidator<ConnectWhatsAppRequest>
{
    /// <summary>Initialises a new instance.</summary>
    public ConnectWhatsAppRequestValidator()
    {
        RuleFor(request => request.Code)
            .NotEmpty().WithMessage("The signup response is missing its authorisation code.")
            .MaximumLength(1024).WithMessage("The authorisation code is not valid.");

        RuleFor(request => request.WabaId)
            .NotEmpty().WithMessage("Select a WhatsApp Business Account.")
            .Must(MetaIdentifiers.IsPlausible)
            .WithMessage("The WhatsApp Business Account identifier is not valid.");

        RuleFor(request => request.PhoneNumberId)
            .NotEmpty().WithMessage("Select a phone number.")
            .Must(MetaIdentifiers.IsPlausible)
            .WithMessage("The phone number identifier is not valid.");
    }
}

/// <summary>Validates <see cref="ManualConnectWhatsAppRequest"/>.</summary>
public sealed class ManualConnectWhatsAppRequestValidator : AbstractValidator<ManualConnectWhatsAppRequest>
{
    /// <summary>Initialises a new instance.</summary>
    public ManualConnectWhatsAppRequestValidator()
    {
        // Only shape is checked. Whether the token actually works is settled by the verification
        // call to Meta, and no amount of local validation can substitute for that answer.
        RuleFor(request => request.AccessToken)
            .NotEmpty().WithMessage("Paste the system-user access token.")
            .MinimumLength(32).WithMessage("That does not look like a Meta access token.")
            .MaximumLength(2048).WithMessage("The access token is too long.");

        RuleFor(request => request.WabaId)
            .NotEmpty().WithMessage("Enter the WhatsApp Business Account identifier.")
            .Must(MetaIdentifiers.IsPlausible)
            .WithMessage("The WhatsApp Business Account identifier is not valid.");

        RuleFor(request => request.PhoneNumberId)
            .NotEmpty().WithMessage("Enter the phone number identifier.")
            .Must(MetaIdentifiers.IsPlausible)
            .WithMessage("The phone number identifier is not valid.");
    }
}

/// <summary>Shape checks for the numeric identifiers Meta issues.</summary>
internal static class MetaIdentifiers
{
    /// <summary>Longest identifier Meta is known to issue, with headroom.</summary>
    private const int MaximumLength = 32;

    /// <summary>
    /// Whether a value looks like a Graph object identifier.
    /// <para>
    /// Digits only. These identifiers are interpolated straight into a Graph URL, so rejecting
    /// anything else here is what stops a crafted value from reshaping the request path.
    /// </para>
    /// </summary>
    /// <param name="value">Candidate identifier.</param>
    public static bool IsPlausible(string? value) =>
        value is { Length: > 0 and <= MaximumLength } && value.All(char.IsAsciiDigit);
}
