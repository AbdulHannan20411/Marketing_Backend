using System.Reflection;
using AwesomeAssertions;
using Marketing.Application.DTOs.Workspace;
using Marketing.Application.Services.Workspace;
using Marketing.Common.Exceptions;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.UnitTests.Services;

/// <summary>
/// Covers the reason rules. The rest of deactivation - password, ownership, revoking every session -
/// is exercised through the endpoint; these are the decisions with no dependencies, and the ones
/// that decide whether the churn data is worth anything.
/// </summary>
public sealed class WorkspaceDeactivationReasonTests
{
    private static readonly MethodInfo ValidateMethod =
        typeof(WorkspaceDeactivationService)
            .GetMethod("ValidateReason", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void Validate(DeactivationReason reason, string? details)
    {
        try
        {
            ValidateMethod.Invoke(null, [new WorkspaceDeactivationRequest(reason, details, "irrelevant")]);
        }
        catch (TargetInvocationException invocation) when (invocation.InnerException is not null)
        {
            throw invocation.InnerException;
        }
    }

    [Theory]
    [InlineData(DeactivationReason.TooExpensive)]
    [InlineData(DeactivationReason.MissingFeatures)]
    [InlineData(DeactivationReason.SwitchingProvider)]
    [InlineData(DeactivationReason.NoLongerNeeded)]
    [InlineData(DeactivationReason.TemporaryPause)]
    public void A_specific_reason_needs_no_explanation(DeactivationReason reason)
    {
        Validate(reason, details: null);
    }

    [Fact]
    public void Something_else_without_an_explanation_is_refused()
    {
        // "Other" with nothing said is the same as no answer, and this is the one moment a
        // departing customer is willing to tell you why.
        var act = () => Validate(DeactivationReason.Other, details: null);

        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void Whitespace_does_not_count_as_an_explanation()
    {
        var act = () => Validate(DeactivationReason.Other, details: "   ");

        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void Something_else_with_an_explanation_is_accepted()
    {
        Validate(DeactivationReason.Other, details: "Merging with another company.");
    }

    [Fact]
    public void A_reason_outside_the_agreed_set_is_refused()
    {
        // The list is shared with the client. A value it never offers arrived from somewhere else,
        // and storing it would put a number in the churn report that means nothing.
        var act = () => Validate((DeactivationReason)99, details: null);

        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void Temporary_pause_is_one_of_the_offered_reasons()
    {
        // Worth a test of its own: it is the reason people would otherwise misreport, and the
        // accounts most likely to come back.
        Enum.IsDefined(DeactivationReason.TemporaryPause).Should().BeTrue();
    }
}
