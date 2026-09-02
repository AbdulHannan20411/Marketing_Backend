using System.Reflection;
using AwesomeAssertions;
using Marketing.Application.DTOs.Billing;

namespace Marketing.UnitTests.Services;

/// <summary>
/// Guards the boundary between what a workspace may do and what it pays.
/// </summary>
/// <remarks>
/// <see cref="EntitlementsSnapshot"/> is served without a permission requirement, so every field on
/// it is readable by any member including an employee. A billing field added here would leak the
/// workspace's costs to everyone in it, silently and without any endpoint changing.
/// </remarks>
public sealed class EntitlementsSnapshotTests
{
    private static readonly string[] BillingFieldNames =
    [
        "Amount",
        "Currency",
        "BillingCycle",
        "NextRenewalAt",
        "AutoRenew",
        "MonthlyPrice",
        "YearlyPrice",
        "DiscountPercent",
        "SeatsPurchased",
    ];

    [Fact]
    public void The_entitlements_snapshot_carries_nothing_about_money()
    {
        // The test that matters. This type is readable without a billing permission, so anything
        // priced on it is disclosed to every employee in the workspace.
        var properties = typeof(EntitlementsSnapshot)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToList();

        properties.Should().NotIntersectWith(BillingFieldNames);
    }

    [Fact]
    public void It_carries_what_the_shell_needs_to_render()
    {
        // The other half: stripping too much would leave the shell unable to decide what exists,
        // which is the failure this endpoint was added to fix.
        var properties = typeof(EntitlementsSnapshot)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToList();

        properties.Should().Contain(["PlanName", "Status", "ExpiresAt", "Modules", "Limits", "Usage"]);
    }

    [Fact]
    public void The_billing_snapshot_still_carries_the_money()
    {
        // The sibling type keeps everything and keeps its permission. Splitting the endpoint must
        // not quietly strip the screen that is supposed to show what the workspace pays.
        var subscriptionProperties = typeof(SubscriptionResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToList();

        subscriptionProperties.Should().Contain(["Amount", "Currency", "AutoRenew", "NextRenewalAt"]);
    }
}
