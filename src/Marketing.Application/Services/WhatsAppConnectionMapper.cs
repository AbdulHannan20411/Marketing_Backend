using Marketing.Application.DTOs.WhatsApp;
using Marketing.DataAccess.Entities;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services;

/// <summary>
/// Turns a stored connection into the shape the connection screen renders.
/// </summary>
/// <remarks>
/// Shared rather than written once per service. Two services return this response - one reads it,
/// one writes it - and when the mapping lived in both, a field added to the contract reached the
/// client from one endpoint and not the other.
/// <para>
/// Mapped in memory rather than projected in SQL because the onboarding steps are a JSON document
/// on the row: the database can return it, but it cannot build the derived "still running" answer
/// the client polls on.
/// </para>
/// </remarks>
public static class WhatsAppConnectionMapper
{
    /// <summary>Projects a connection entity onto its response.</summary>
    /// <param name="connection">The stored connection.</param>
    public static WhatsAppConnectionResponse ToResponse(this WhatsAppConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return new WhatsAppConnectionResponse(
            connection.Status,
            connection.DisplayPhoneNumber,
            connection.VerifiedName,
            connection.BusinessProfileAbout,
            connection.BusinessCategory,
            connection.QualityRating,
            connection.MessagingLimit,
            connection.MessagesLast24h,
            connection.MessagingTier,
            connection.ConnectedAt,
            connection.TokenExpiresAt,
            ToOnboarding(connection),
            connection.WebhookHealthy,
            connection.TemplateNamespaceAlias);
    }

    /// <summary>Projects the stored steps, in the order they run.</summary>
    private static ConnectionOnboardingResponse ToOnboarding(WhatsAppConnection connection)
    {
        if (connection.OnboardingSteps.Count == 0)
        {
            // A connection made before this feature existed, or one never attempted. Reported as
            // idle rather than as an empty running state, so the client renders nothing rather
            // than an empty progress panel.
            return ConnectionOnboardingResponse.Idle();
        }

        var steps = connection.OnboardingSteps
            .OrderBy(step => step.Step)
            .Select(step => new OnboardingStepResponse(
                step.Step, step.Status, step.Code, step.Message, step.CompletedAt))
            .ToList();

        // The step the client should point at: whatever is happening now, or the one that stopped
        // the run. Null once every step has settled successfully.
        var current = steps.FirstOrDefault(step =>
            step.Status is OnboardingStepStatus.Running
                or OnboardingStepStatus.Pending
                or OnboardingStepStatus.Failed);

        // Taken from the connection's own status rather than re-derived from the list, so the
        // client and the server cannot disagree about whether to keep polling.
        var running = connection.Status == ConnectionStatus.Pending;

        return new ConnectionOnboardingResponse(running, current?.Step, steps);
    }
}
