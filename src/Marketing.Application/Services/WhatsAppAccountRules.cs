using Marketing.Common.Exceptions;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services;

/// <summary>Rules about naming a workspace's WhatsApp numbers, and describing their failures.</summary>
public static class WhatsAppAccountRules
{
    /// <summary>Longest label a number may have.</summary>
    public const int MaximumLabelLength = 40;

    /// <summary>Refuses a label that is empty or too long.</summary>
    /// <param name="label">The label as supplied.</param>
    /// <returns>The label, trimmed.</returns>
    /// <exception cref="ValidationException">It is empty or over the limit.</exception>
    public static string ValidateLabel(string? label)
    {
        var trimmed = label?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            throw new ValidationException("label", "Give this number a name.");
        }

        if (trimmed.Length > MaximumLabelLength)
        {
            throw new ValidationException(
                "label",
                $"Keep the name to {MaximumLabelLength} characters or fewer.");
        }

        return trimmed;
    }

    /// <summary>What a number is called before anyone names it.</summary>
    /// <param name="verifiedName">Meta's verified business name.</param>
    /// <param name="displayPhoneNumber">The number itself.</param>
    public static string DefaultLabel(string? verifiedName, string? displayPhoneNumber)
    {
        var name = !string.IsNullOrWhiteSpace(verifiedName) ? verifiedName.Trim()
            : !string.IsNullOrWhiteSpace(displayPhoneNumber) ? displayPhoneNumber.Trim()
            : "WhatsApp";

        return name.Length <= MaximumLabelLength ? name : name[..MaximumLabelLength];
    }

    /// <summary>
    /// A label no other number in the workspace has, compared without regard to case.
    /// </summary>
    /// <param name="wanted">The label asked for.</param>
    /// <param name="taken">Every other number's label.</param>
    /// <param name="allowSuffix">
    /// Whether to resolve a clash by appending " 2", " 3" - right for a name the server chose, wrong
    /// for one a person typed, who should be told rather than silently renamed.
    /// </param>
    /// <exception cref="BusinessRuleException">The label is taken and no suffix is allowed.</exception>
    public static string UniqueLabel(string wanted, IEnumerable<string> taken, bool allowSuffix)
    {
        ArgumentNullException.ThrowIfNull(taken);

        var label = ValidateLabel(wanted);
        var existing = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);

        if (!existing.Contains(label))
        {
            return label;
        }

        if (!allowSuffix)
        {
            throw new BusinessRuleException(
                "whatsapp_label_taken",
                $"Another WhatsApp number in this workspace is already called \"{label}\". Choose a different name.");
        }

        for (var suffix = 2; ; suffix++)
        {
            var tail = $" {suffix}";
            var stem = label.Length + tail.Length <= MaximumLabelLength
                ? label
                : label[..(MaximumLabelLength - tail.Length)];
            var candidate = stem + tail;

            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>A failed onboarding step, in words for the health panel. Never Meta's own text.</summary>
    /// <param name="step">The step that failed.</param>
    public static string PlainError(OnboardingStep step) => step switch
    {
        OnboardingStep.Subscribe => "Meta would not send this number's messages to the app. Reconnect it.",
        OnboardingStep.Register => "Meta would not register this number for sending.",
        OnboardingStep.Profile => "Meta would not return this number's details. Try syncing it.",
        _ => "Connecting this number did not finish. Try again.",
    };
}
