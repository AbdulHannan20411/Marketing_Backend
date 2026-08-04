using AwesomeAssertions;
using Marketing.Infrastructure.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Marketing.UnitTests.Infrastructure;

/// <summary>
/// Covers the behaviour that matters most about the cache: what it does when Redis is not there.
/// </summary>
public sealed class RedisCacheServiceTests
{
    private sealed record CachedPayload(string Value);

    private static RedisCacheService CreateUnavailableCache() =>
        new(
            multiplexer: null,
            Options.Create(new RedisOptions { ConnectionString = "localhost:6379", Enabled = true }),
            new FixedDateTimeProvider(DateTimeOffset.UtcNow),
            NullLogger<RedisCacheService>.Instance);

    [Fact]
    public void An_absent_connection_reports_the_cache_as_unavailable()
    {
        CreateUnavailableCache().IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task A_read_against_an_unavailable_cache_is_a_miss_rather_than_a_failure()
    {
        var cache = CreateUnavailableCache();

        var result = await cache.GetAsync<CachedPayload>("marketing:platform:probe");

        // The caller falls through to PostgreSQL. A throw here would turn a cache outage into an
        // API outage, which is the whole failure mode this design exists to prevent.
        result.Should().BeNull();
    }

    [Fact]
    public async Task A_write_against_an_unavailable_cache_is_silently_discarded()
    {
        var cache = CreateUnavailableCache();

        var act = async () => await cache.SetAsync("marketing:platform:probe", new CachedPayload("x"));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetOrCreate_falls_through_to_the_factory_when_the_cache_is_unavailable()
    {
        var cache = CreateUnavailableCache();
        var factoryCalls = 0;

        var result = await cache.GetOrCreateAsync(
            "marketing:platform:probe",
            _ =>
            {
                factoryCalls++;
                return Task.FromResult<CachedPayload?>(new CachedPayload("from-database"));
            });

        result!.Value.Should().Be("from-database");
        factoryCalls.Should().Be(1);
    }

    [Fact]
    public async Task GetOrCreate_does_not_cache_a_null_result()
    {
        var cache = CreateUnavailableCache();

        var result = await cache.GetOrCreateAsync<CachedPayload>(
            "marketing:platform:missing",
            _ => Task.FromResult<CachedPayload?>(null));

        result.Should().BeNull();
    }

    [Fact]
    public async Task Removal_against_an_unavailable_cache_does_not_throw()
    {
        var cache = CreateUnavailableCache();

        var removeOne = async () => await cache.RemoveAsync("marketing:platform:probe");
        var removeMany = async () => await cache.RemoveByPrefixAsync("marketing:t:abc:");

        await removeOne.Should().NotThrowAsync();
        await removeMany.Should().NotThrowAsync();
    }

    [Fact]
    public void A_disabled_cache_never_reports_itself_as_available()
    {
        var cache = new RedisCacheService(
            multiplexer: null,
            Options.Create(new RedisOptions { ConnectionString = "localhost:6379", Enabled = false }),
            new FixedDateTimeProvider(DateTimeOffset.UtcNow),
            NullLogger<RedisCacheService>.Instance);

        cache.IsAvailable.Should().BeFalse();
    }
}
