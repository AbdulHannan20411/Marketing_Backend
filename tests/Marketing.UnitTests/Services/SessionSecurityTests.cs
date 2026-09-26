using AwesomeAssertions;
using Marketing.Application.Configurations;
using Marketing.Application.Services.Security;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Responses;
using Marketing.DataAccess.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.UnitTests.Services;

/// <summary>Runs queries against in-memory lists, so the real LINQ - projections and all - is exercised.</summary>
internal sealed class InMemoryQueryExecutor : IQueryExecutor
{
    public int Queries { get; private set; }

    public Task<IReadOnlyList<TResult>> ToListAsync<TResult>(IQueryable<TResult> query, CancellationToken cancellationToken = default)
    {
        Queries++;
        return Task.FromResult<IReadOnlyList<TResult>>([.. query]);
    }

    public Task<TResult?> FirstOrDefaultAsync<TResult>(IQueryable<TResult> query, CancellationToken cancellationToken = default)
    {
        Queries++;
        return Task.FromResult(query.FirstOrDefault());
    }

    public Task<int> CountAsync<TResult>(IQueryable<TResult> query, CancellationToken cancellationToken = default)
    {
        Queries++;
        return Task.FromResult(query.Count());
    }

    public Task<int> SumAsync(IQueryable<int> query, CancellationToken cancellationToken = default) =>
        Task.FromResult(query.Sum());

    public IAsyncEnumerable<TResult> StreamAsync<TResult>(IQueryable<TResult> query, CancellationToken cancellationToken = default)
    {
        Queries++;

        // Materialised up front, exactly as the provider composes the query up front: a stream
        // that deferred composition would hide the one thing the export tests check, which is
        // that an unsortable field is refused before a single row is written.
        var rows = query.ToList();

        return ToAsync(rows);
    }

    /// <summary>Wraps a materialised list as an async sequence.</summary>
    private static async IAsyncEnumerable<TResult> ToAsync<TResult>(IEnumerable<TResult> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }

    public Task<PagedResult<TResult>> ToPagedAsync<TResult>(IQueryable<TResult> query, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        Queries++;

        var total = query.Count();
        var items = query.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return Task.FromResult(new PagedResult<TResult>(items, total, page, pageSize));
    }
}

/// <summary>Reading a device out of its user agent.</summary>
public sealed class UserAgentParserTests
{
    [Theory]
    [InlineData(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36",
        "Chrome", "Windows", "desktop")]
    [InlineData(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36 Edg/128.0.0.0",
        "Edge", "Windows", "desktop")]
    [InlineData(
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_5) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15",
        "Safari", "macOS", "desktop")]
    [InlineData(
        "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Mobile Safari/537.36",
        "Chrome", "Android", "mobile")]
    [InlineData(
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1",
        "Safari", "iOS", "mobile")]
    [InlineData(
        "Mozilla/5.0 (iPad; CPU OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1",
        "Safari", "iOS", "tablet")]
    [InlineData(
        "Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:129.0) Gecko/20100101 Firefox/129.0",
        "Firefox", "Linux", "desktop")]
    public void Families_are_read_without_versions(string agent, string browser, string os, string type)
    {
        // Families only: a version in the label would make one laptop look like a new device every
        // time its browser updated.
        var device = UserAgentParser.Parse(agent);

        device.Browser.Should().Be(browser);
        device.OperatingSystem.Should().Be(os);
        device.DeviceType.Should().Be(type);
        device.Label.Should().Be($"{browser} / {os}");
    }

    [Fact]
    public void No_user_agent_is_an_unknown_desktop_rather_than_an_error()
    {
        UserAgentParser.Parse(null).Label.Should().Be("Other / Other");
    }
}

/// <summary>Turning sign-in signals into a risk level a Super Admin can act on.</summary>
public sealed class RiskScorerTests
{
    private static RiskSignals Signals(
        int devices = 1,
        int activeSessions = 1,
        int displaced = 0,
        int ips = 1,
        int locations = 1,
        int failures = 0,
        bool newDevice = false) =>
        new(devices, DeviceLimit: 3, DeviceWindowDays: 30, activeSessions, displaced, ips, locations, failures, newDevice);

    [Fact]
    public void One_person_on_one_laptop_is_low_risk()
    {
        var assessment = RiskScorer.Score(Signals());

        assessment.Level.Should().Be(RiskLevel.Low);
        assessment.Reasons.Should().BeEmpty();
    }

    [Fact]
    public void A_laptop_and_a_phone_is_still_low()
    {
        // Two devices is ordinary. A score that flags normal people is a score nobody reads.
        RiskScorer.Score(Signals(devices: 2)).Level.Should().Be(RiskLevel.Low);
    }

    [Fact]
    public void The_shared_login_from_the_brief_is_high_risk_with_every_reason_named()
    {
        // Four devices, three displacements, two cities, and a new device today.
        var assessment = RiskScorer.Score(Signals(devices: 4, displaced: 3, locations: 2, newDevice: true));

        assessment.Level.Should().Be(RiskLevel.High);
        assessment.Reasons.Should().Contain(reason => reason.Contains("4 devices"));
        assessment.Reasons.Should().Contain(reason => reason.Contains("pushed another session off"));
        assessment.Reasons.Should().Contain(reason => reason.Contains("2 locations"));
        assessment.Reasons.Should().Contain("New device in the last 24 hours");
    }

    [Fact]
    public void Repeated_displacement_alone_is_enough_to_look()
    {
        // With one session per account, people sharing a login sign each other out. That trace is the
        // strongest single signal there is.
        RiskScorer.Score(Signals(displaced: 3)).Level.Should().Be(RiskLevel.Medium);
        RiskScorer.Score(Signals(displaced: 6, devices: 4)).Level.Should().Be(RiskLevel.High);
    }

    [Fact]
    public void Every_point_has_a_reason()
    {
        // A score nobody can explain cannot be defended to the customer it was used against.
        var assessment = RiskScorer.Score(Signals(devices: 5, activeSessions: 2, displaced: 1, ips: 4, locations: 3, failures: 6, newDevice: true));

        assessment.Reasons.Should().HaveCount(7);
        assessment.Score.Should().BeGreaterThanOrEqualTo(RiskScorer.HighThreshold);
    }
}

/// <summary>Recording sessions, spotting new devices, and knowing which sessions are still alive.</summary>
public sealed class SessionTrackerTests : IDisposable
{
    private const long TenantId = 7301;
    private const long UserId = 8301;
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 14, 31, 0, TimeSpan.Zero);

    private readonly List<UserSession> _sessionRows = [];
    private readonly List<SecurityEvent> _eventRows = [];
    private readonly List<RefreshToken> _tokenRows = [];

    private readonly IRepository<UserSession> _sessions = Substitute.For<IRepository<UserSession>>();
    private readonly IRepository<SecurityEvent> _events = Substitute.For<IRepository<SecurityEvent>>();
    private readonly IRefreshTokenRepository _refreshTokens = Substitute.For<IRefreshTokenRepository>();
    private readonly InMemoryQueryExecutor _queries = new();
    private readonly StubRequestContext _request = new()
    {
        UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36",
        IpAddress = "39.45.12.8",
        DeviceId = "device-laptop",
        Location = "Lahore, PK",
    };

    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    private readonly User _user = new()
    {
        Id = UserId,
        TenantId = TenantId,
        Email = "ahmed@abc.example",
        NormalizedEmail = "AHMED@ABC.EXAMPLE",
        DisplayName = "Ahmed",
        PasswordHash = "hash",
    };

    public SessionTrackerTests()
    {
        _sessions.Query(Arg.Any<bool>()).Returns(_ => _sessionRows.AsQueryable());
        _events.Query(Arg.Any<bool>()).Returns(_ => _eventRows.AsQueryable());
        _refreshTokens.Query(Arg.Any<bool>()).Returns(_ => _tokenRows.AsQueryable());

        _sessions.When(repository => repository.Add(Arg.Any<UserSession>()))
            .Do(call => _sessionRows.Add(call.Arg<UserSession>()!));
        _events.When(repository => repository.Add(Arg.Any<SecurityEvent>()))
            .Do(call => _eventRows.Add(call.Arg<SecurityEvent>()!));
    }

    public void Dispose() => _cache.Dispose();

    private SessionTracker CreateTracker() =>
        new(
            _sessions,
            _events,
            _refreshTokens,
            _queries,
            Substitute.For<IUnitOfWork>(),
            _request,
            Substitute.For<ISecurityAlertService>(),
            _cache,
            new FixedDateTimeProvider(Now),
            Options.Create(new AuthenticationPolicyOptions { MaxDevicesPerUser = 3, DeviceWindowDays = 30 }),
            NullLogger<SessionTracker>.Instance);

    private void PreviousSession(string deviceId, string? location = "Lahore, PK", int daysAgo = 2) =>
        _sessionRows.Add(new UserSession
        {
            Id = _sessionRows.Count + 1,
            TenantId = TenantId,
            UserId = UserId,
            SessionId = Guid.NewGuid(),
            DeviceId = deviceId,
            Location = location,
            LastActivityAt = Now.AddDays(-daysAgo),
        });

    [Fact]
    public async Task The_first_ever_sign_in_is_recorded_but_alarms_nobody()
    {
        var facts = await CreateTracker().StartAsync(_user, Guid.NewGuid(), [], TestContext.Current.CancellationToken);

        facts.IsFirstEver.Should().BeTrue();
        facts.IsNewDevice.Should().BeFalse();

        var session = _sessionRows.Should().ContainSingle().Which;

        session.DeviceLabel.Should().Be("Chrome / Windows");
        session.DeviceId.Should().Be("device-laptop");
        session.IpAddress.Should().Be("39.45.12.8");
        session.Location.Should().Be("Lahore, PK");
        session.LastActivityAt.Should().Be(Now);

        _eventRows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_device_the_account_has_never_used_is_noticed()
    {
        PreviousSession("device-desktop");

        var facts = await CreateTracker().StartAsync(_user, Guid.NewGuid(), [], TestContext.Current.CancellationToken);

        facts.IsNewDevice.Should().BeTrue();
        _eventRows.Should().ContainSingle(securityEvent => securityEvent.Kind == SecurityEventKind.NewDevice);
    }

    [Fact]
    public async Task A_returning_device_is_not_new()
    {
        PreviousSession("device-laptop");

        var facts = await CreateTracker().StartAsync(_user, Guid.NewGuid(), [], TestContext.Current.CancellationToken);

        facts.IsNewDevice.Should().BeFalse();
        _eventRows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_new_city_is_noticed_even_on_a_known_device()
    {
        PreviousSession("device-laptop", location: "Karachi, PK");

        var facts = await CreateTracker().StartAsync(_user, Guid.NewGuid(), [], TestContext.Current.CancellationToken);

        facts.IsNewLocation.Should().BeTrue();
        _eventRows.Should().ContainSingle(securityEvent => securityEvent.Kind == SecurityEventKind.NewLocation);
    }

    [Fact]
    public async Task Passing_the_device_limit_is_recorded()
    {
        PreviousSession("device-a");
        PreviousSession("device-b");
        PreviousSession("device-c");

        var facts = await CreateTracker().StartAsync(_user, Guid.NewGuid(), [], TestContext.Current.CancellationToken);

        facts.DevicesInWindow.Should().Be(4);
        _eventRows.Should().Contain(securityEvent => securityEvent.Kind == SecurityEventKind.DeviceLimitExceeded);
    }

    [Fact]
    public async Task A_displaced_session_is_ended_and_recorded_as_evidence()
    {
        // One session per account: signing in here pushed the other device off. That push is what
        // sharing looks like once sharing is prevented, so it is kept.
        PreviousSession("device-desktop");
        var displaced = _sessionRows[0].SessionId;

        await CreateTracker().StartAsync(_user, Guid.NewGuid(), [displaced], TestContext.Current.CancellationToken);

        _sessionRows[0].RevokedAt.Should().Be(Now);
        _sessionRows[0].RevokedReason.Should().Be("signed_in_elsewhere");

        var evidence = _eventRows.Should().ContainSingle(securityEvent => securityEvent.Kind == SecurityEventKind.SessionDisplaced).Which;

        evidence.SessionId.Should().Be(displaced);
        evidence.Detail.Should().Contain("Chrome / Windows");
    }

    [Fact]
    public async Task A_session_without_a_live_refresh_token_is_not_active()
    {
        var live = Guid.NewGuid();
        var ended = Guid.NewGuid();

        _tokenRows.Add(Token(live, revoked: false));
        _tokenRows.Add(Token(ended, revoked: true));

        var tracker = CreateTracker();

        (await tracker.IsActiveAsync(live, TestContext.Current.CancellationToken)).Should().BeTrue();
        (await tracker.IsActiveAsync(ended, TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task Liveness_is_cached_so_it_is_not_a_query_per_request()
    {
        var live = Guid.NewGuid();
        _tokenRows.Add(Token(live, revoked: false));

        var tracker = CreateTracker();

        for (var request = 0; request < 5; request++)
        {
            await tracker.IsActiveAsync(live, TestContext.Current.CancellationToken);
        }

        _queries.Queries.Should().Be(1);
    }

    [Fact]
    public async Task Ending_a_session_takes_effect_on_its_next_request_not_after_the_cache_expires()
    {
        var sessionId = Guid.NewGuid();
        var token = Token(sessionId, revoked: false);
        _tokenRows.Add(token);

        var row = new UserSession { Id = 1, TenantId = TenantId, UserId = UserId, SessionId = sessionId, LastActivityAt = Now };

        _refreshTokens.FindBySessionAsync(sessionId, Arg.Any<CancellationToken>()).Returns([token]);

        var tracker = CreateTracker();

        (await tracker.IsActiveAsync(sessionId, TestContext.Current.CancellationToken)).Should().BeTrue();

        await tracker.RevokeAsync(row, "revoked_by_admin", revokedByUserId: 1, TestContext.Current.CancellationToken);

        row.RevokedAt.Should().Be(Now);
        token.RevokedOn.Should().Be(Now);
        (await tracker.IsActiveAsync(sessionId, TestContext.Current.CancellationToken)).Should().BeFalse();

        _eventRows.Should().ContainSingle(securityEvent => securityEvent.Kind == SecurityEventKind.SessionRevoked);
    }

    private RefreshToken Token(Guid sessionId, bool revoked) =>
        new()
        {
            Id = _tokenRows.Count + 1,
            UserId = UserId,
            TenantId = TenantId,
            SessionId = sessionId,
            TokenHash = Guid.NewGuid().ToString("N"),
            ExpiresOn = Now.AddDays(7),
            RevokedOn = revoked ? Now.AddMinutes(-5) : null,
        };
}
