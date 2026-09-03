using AwesomeAssertions;
using Marketing.Application.Services;
using Marketing.DataAccess.Entities;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.UnitTests.Services;

/// <summary>
/// Reporting how far a WhatsApp connection attempt has got.
/// </summary>
/// <remarks>
/// Written because "could not connect" is not an actionable message. Subscribing to webhooks,
/// registering the number and reading the profile fail for unrelated reasons with unrelated
/// remedies, and an administrator told only that something went wrong has nowhere to go.
/// </remarks>
public sealed class OnboardingMappingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 20, 0, 0, TimeSpan.Zero);

    private static WhatsAppConnection Connection(
        ConnectionStatus status,
        params (OnboardingStep Step, OnboardingStepStatus Status)[] steps) =>
        new()
        {
            Status = status,
            OnboardingSteps = [.. steps.Select(entry => new WhatsAppOnboardingStep
            {
                Step = entry.Step,
                Status = entry.Status,
                CompletedAt = entry.Status is OnboardingStepStatus.Pending ? null : Now,
            })],
        };

    [Fact]
    public void A_connection_that_was_never_attempted_reports_idle()
    {
        // Idle rather than an empty running state: the client renders nothing at all instead of an
        // empty progress panel. Connections stored before this feature existed take this path too.
        var onboarding = new WhatsAppConnection { Status = ConnectionStatus.Disconnected }
            .ToResponse().Onboarding;

        onboarding.Running.Should().BeFalse();
        onboarding.CurrentStep.Should().BeNull();
        onboarding.Steps.Should().BeEmpty();
    }

    [Fact]
    public void A_pending_connection_is_running_and_points_at_the_live_step()
    {
        var onboarding = Connection(
                ConnectionStatus.Pending,
                (OnboardingStep.Token, OnboardingStepStatus.Succeeded),
                (OnboardingStep.Subscribe, OnboardingStepStatus.Running),
                (OnboardingStep.Register, OnboardingStepStatus.Pending),
                (OnboardingStep.Profile, OnboardingStepStatus.Pending))
            .ToResponse().Onboarding;

        // Running is taken from the connection's own status, not re-derived from the list, so the
        // client polling on it cannot disagree with the server about when to stop.
        onboarding.Running.Should().BeTrue();
        onboarding.CurrentStep.Should().Be(OnboardingStep.Subscribe);
        onboarding.Steps.Should().HaveCount(4);
    }

    [Fact]
    public void A_finished_connection_stops_running_and_names_no_step()
    {
        var onboarding = Connection(
                ConnectionStatus.Connected,
                (OnboardingStep.Token, OnboardingStepStatus.Succeeded),
                (OnboardingStep.Subscribe, OnboardingStepStatus.Succeeded),
                (OnboardingStep.Register, OnboardingStepStatus.Skipped),
                (OnboardingStep.Profile, OnboardingStepStatus.Succeeded))
            .ToResponse().Onboarding;

        onboarding.Running.Should().BeFalse();
        onboarding.CurrentStep.Should().BeNull();
    }

    [Fact]
    public void A_skipped_step_does_not_make_the_connection_look_broken()
    {
        // The case this distinction exists for. Every Meta test number, and every number onboarded
        // through Embedded Signup, is already registered and refuses a second attempt. Reporting
        // that as a failure made every test number look broken.
        var connection = Connection(
            ConnectionStatus.Connected,
            (OnboardingStep.Register, OnboardingStepStatus.Skipped));

        var step = connection.ToResponse().Onboarding.Steps.Single();

        step.Status.Should().Be(OnboardingStepStatus.Skipped);
        step.Code.Should().BeNull("a skip is not a failure and has no remedy to offer");
    }

    [Fact]
    public void A_failed_step_is_the_one_the_client_is_pointed_at()
    {
        var onboarding = Connection(
                ConnectionStatus.Error,
                (OnboardingStep.Token, OnboardingStepStatus.Succeeded),
                (OnboardingStep.Subscribe, OnboardingStepStatus.Failed),
                (OnboardingStep.Register, OnboardingStepStatus.Pending),
                (OnboardingStep.Profile, OnboardingStepStatus.Pending))
            .ToResponse().Onboarding;

        onboarding.Running.Should().BeFalse();
        onboarding.CurrentStep.Should().Be(OnboardingStep.Subscribe);

        // The steps after a failure stay pending rather than being marked failed. They were never
        // attempted, and calling them failed tells the administrator four things are broken when
        // one is.
        onboarding.Steps
            .Where(step => step.Step > OnboardingStep.Subscribe)
            .Should().OnlyContain(step => step.Status == OnboardingStepStatus.Pending);
    }

    [Fact]
    public void Steps_are_reported_in_the_order_they_run()
    {
        // Stored order is whatever the writer happened to append. The panel reads top to bottom,
        // so the contract fixes the order rather than leaving it to the persistence layer.
        var onboarding = Connection(
                ConnectionStatus.Pending,
                (OnboardingStep.Profile, OnboardingStepStatus.Pending),
                (OnboardingStep.Token, OnboardingStepStatus.Succeeded),
                (OnboardingStep.Register, OnboardingStepStatus.Pending),
                (OnboardingStep.Subscribe, OnboardingStepStatus.Running))
            .ToResponse().Onboarding;

        onboarding.Steps.Select(step => step.Step).Should().ContainInOrder(
            OnboardingStep.Token,
            OnboardingStep.Subscribe,
            OnboardingStep.Register,
            OnboardingStep.Profile);

        onboarding.CurrentStep.Should().Be(OnboardingStep.Subscribe);
    }
}
