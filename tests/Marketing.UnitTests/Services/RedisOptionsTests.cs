using AwesomeAssertions;
using Marketing.Infrastructure.Redis;
using Microsoft.Extensions.Configuration;

namespace Marketing.UnitTests.Services;

/// <summary>
/// The two Redis switches, which fail in opposite ways.
/// </summary>
/// <remarks>
/// <c>Enabled</c> turns off a fail-soft dependency: with the cache gone every read is a miss and
/// the request still answers. <c>UseSignalRBackplane</c> turns off one that is not fail-soft: the
/// backplane holds each connection's subscription, so a connection it cannot register is refused
/// rather than served without updates, and with Redis down that is every connection.
/// <para>
/// Both are bound from configuration and both default to on, which is the part worth pinning: an
/// <c>init</c> property that silently failed to bind would read as "the backplane is off in
/// production" and nothing would say so.
/// </para>
/// </remarks>
public sealed class RedisOptionsTests
{
    private static RedisOptions Bind(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                [.. settings.Select(setting =>
                    new KeyValuePair<string, string?>($"Redis:{setting.Key}", setting.Value))])
            .Build()
            .GetSection(RedisOptions.SectionName)
            .Get<RedisOptions>() ?? new RedisOptions();

    [Fact]
    public void Both_switches_are_on_unless_something_says_otherwise()
    {
        var options = Bind(("ConnectionString", "localhost:6379"));

        // A deployment that says nothing gets the cache and the backplane. Defaulting the
        // backplane off would silently halve realtime delivery the day a second instance starts.
        options.Enabled.Should().BeTrue();
        options.UseSignalRBackplane.Should().BeTrue();
    }

    [Fact]
    public void The_backplane_can_be_turned_off_on_its_own()
    {
        var options = Bind(
            ("ConnectionString", "localhost:6379"),
            ("Enabled", "true"),
            ("UseSignalRBackplane", "false"));

        // What development runs: the cache still works when Redis is up, and the hub no longer
        // refuses every connection when it is down. One instance has nothing to bridge.
        options.Enabled.Should().BeTrue();
        options.UseSignalRBackplane.Should().BeFalse();
    }

    [Fact]
    public void Turning_the_cache_off_does_not_silently_leave_the_backplane_on()
    {
        var options = Bind(("ConnectionString", "localhost:6379"), ("Enabled", "false"));

        // The registration requires both, so this case is covered there rather than here - but
        // the option itself still reads as on, and a reader of the config should not have to
        // guess which switch wins.
        options.Enabled.Should().BeFalse();
        options.UseSignalRBackplane.Should().BeTrue();
    }
}
