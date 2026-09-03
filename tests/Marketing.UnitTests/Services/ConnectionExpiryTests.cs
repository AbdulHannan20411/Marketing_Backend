using AwesomeAssertions;
using Marketing.Application.DTOs.WhatsApp;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.UnitTests.Services;

/// <summary>
/// Reporting a WhatsApp credential that has stopped working.
/// </summary>
/// <remarks>
/// Embedded Signup issues a token with a finite life. Before this rule existed the expiry was
/// stored and never read, so a lapsed connection kept reporting itself as connected and the first
/// sign of trouble was a campaign failing with an opaque 401 - indistinguishable, at that point,
/// from a revoked permission.
/// </remarks>
public sealed class ConnectionExpiryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private static WhatsAppConnectionResponse Connection(
        DateTimeOffset? expiresAt,
        ConnectionStatus status = ConnectionStatus.Connected) =>
        new(
            status,
            DisplayPhoneNumber: "+92 336 7890092",
            VerifiedName: "Test Number",
            BusinessProfileAbout: string.Empty,
            BusinessCategory: string.Empty,
            QualityRating.Green,
            MessagingLimit: 1000,
            MessagesLast24h: 0,
            MessagingTier.Tier250,
            ConnectedAt: Now.AddDays(-30),
            TokenExpiresAt: expiresAt,
            ConnectionOnboardingResponse.Idle(),
            WebhookHealthy: true,
            TemplateNamespaceAlias: string.Empty);

    [Fact]
    public void A_lapsed_token_is_reported_as_errored()
    {
        // The whole point: the screen must say something is wrong before a campaign says it.
        Connection(Now.AddSeconds(-1)).WithExpiryApplied(Now)
            .Status.Should().Be(ConnectionStatus.Error);
    }

    [Fact]
    public void A_token_expiring_later_still_reports_connected()
    {
        // It still sends. Marking it errored would take a working screen away from a customer who
        // can carry on using it, which is why the date travels separately for the warning.
        var connection = Connection(Now.AddDays(7)).WithExpiryApplied(Now);

        connection.Status.Should().Be(ConnectionStatus.Connected);
        connection.TokenExpiresAt.Should().Be(Now.AddDays(7));
    }

    [Fact]
    public void A_token_with_no_expiry_is_left_alone()
    {
        // Null means a credential with no stated end - a system-user token - not one that expired
        // at the epoch. Treating null as "long ago" would break every manually connected account.
        Connection(expiresAt: null).WithExpiryApplied(Now)
            .Status.Should().Be(ConnectionStatus.Connected);
    }

    [Theory]
    [InlineData(ConnectionStatus.Disconnected)]
    [InlineData(ConnectionStatus.Pending)]
    [InlineData(ConnectionStatus.Error)]
    public void A_status_other_than_connected_survives(ConnectionStatus status)
    {
        // Already-specific answers are not overwritten. "Pending" and "Disconnected" say why in a
        // way that "Error" does not, and a connection can be both lapsed and never finished.
        Connection(Now.AddDays(-1), status).WithExpiryApplied(Now)
            .Status.Should().Be(status);
    }

    [Fact]
    public void Expiry_exactly_now_counts_as_lapsed()
    {
        // The boundary picked deliberately: a token whose last valid instant has arrived is treated
        // as gone, because the alternative is a window where the product claims it works and Meta
        // disagrees.
        Connection(Now).WithExpiryApplied(Now).Status.Should().Be(ConnectionStatus.Error);
    }
}
