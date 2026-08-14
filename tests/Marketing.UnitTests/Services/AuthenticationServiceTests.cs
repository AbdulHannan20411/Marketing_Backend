using AwesomeAssertions;
using Marketing.Application.Configurations;
using Marketing.Application.DTOs.Auth;
using Marketing.Application.Services;
using Marketing.Business.Repositories.Interfaces;
using static Marketing.Common.Constants.AppConstants;
using Marketing.Common.Exceptions;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Marketing.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Marketing.UnitTests.Services;

public sealed class AuthenticationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);
    private static readonly long UserId = 1001;
    private static readonly long TenantId = 2001;

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IRefreshTokenRepository _refreshTokens = Substitute.For<IRefreshTokenRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IPasswordHasher _passwordHasher = Substitute.For<IPasswordHasher>();
    private readonly ITokenService _tokenService = Substitute.For<ITokenService>();
    private readonly StubCurrentUser _currentUser = new();
    private readonly IUserTokenRepository _userTokens = Substitute.For<IUserTokenRepository>();
    private readonly IAccountActivationService _activation = Substitute.For<IAccountActivationService>();
    private readonly IPasswordPolicy _passwordPolicy =
        new PasswordPolicy(Options.Create(new AuthenticationPolicyOptions()));
    private readonly FixedDateTimeProvider _clock = new(Now);

    private readonly AuthenticationPolicyOptions _policy = new()
    {
        MaxFailedLoginAttempts = 3,
        LockoutDurationMinutes = 15,
        MaxConcurrentSessions = 5,
    };

    /// <summary>
    /// Configures the permissive default stubs once, at construction.
    /// <para>
    /// They must not live in <see cref="CreateService"/>: NSubstitute lets a broader matcher
    /// registered later replace a narrower one registered earlier, so a catch-all applied at
    /// service-construction time would silently overwrite the specific expectation a test had
    /// already set up.
    /// </para>
    /// </summary>
    public AuthenticationServiceTests()
    {
        _tokenService.CreateAccessToken(Arg.Any<TokenSubject>())
            .Returns(new AccessToken("access-token", Now.AddMinutes(15)));

        _tokenService.CreateRefreshToken()
            .Returns(new RefreshTokenMaterial("refresh-token", "refresh-hash", Now.AddDays(14)));

        _tokenService.HashRefreshToken(Arg.Any<string>())
            .Returns(call => $"{call.Arg<string>()}-hash");

        _refreshTokens.GetActiveSessionsAsync(Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([]);
    }

    private AuthenticationService CreateService()
    {
        return new AuthenticationService(
            _users,
            _refreshTokens,
            _unitOfWork,
            _passwordHasher,
            _tokenService,
            _clock,
            new StubRequestContext(),
            _currentUser,
            _userTokens,
            _activation,
            _passwordPolicy,
            Options.Create(_policy),
            NullLogger<AuthenticationService>.Instance);
    }

    private static User ActiveUser() => new()
    {
        Id = UserId,
        TenantId = TenantId,
        Email = "operator@acme.test",
        NormalizedEmail = "operator@acme.test",
        DisplayName = "Operator",
        PasswordHash = "stored-hash",
        Status = UserStatus.Active,
        SecurityStamp = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Tenant = new Tenant
        {
            Id = TenantId,
            Name = "Acme",
            Slug = "acme",
            ContactEmail = "billing@acme.test",
            Status = TenantStatus.Active,
        },
    };

    [Fact]
    public async Task Valid_credentials_produce_a_token_pair_and_a_persisted_session()
    {
        var user = ActiveUser();

        _users.FindForAuthenticationAsync("operator@acme.test", Arg.Any<CancellationToken>()).Returns(user);
        _passwordHasher.Verify("secret", "stored-hash").Returns((true, false));

        var response = await CreateService()
            .LoginAsync(new LoginRequest("Operator@Acme.test", "secret"), TestContext.Current.CancellationToken);

        response.AccessToken.Should().Be("access-token");
        response.RefreshToken.Should().Be("refresh-token");
        // The token pair carries no profile at all - identity, role and permissions come from the
        // decoded access token, so there is no second copy that can disagree with it.
        typeof(AuthTokens).GetProperty("User").Should().BeNull();
        typeof(AuthTokens).GetProperty("TenantId").Should().BeNull();

        _refreshTokens.Received(1).Add(Arg.Is<RefreshToken>(token =>
            token!.UserId == UserId
            && token.TenantId == TenantId
            && token.TokenHash == "refresh-hash"
            && token.SecurityStamp == user.SecurityStamp));

        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());

        user.LastLoginOn.Should().Be(Now);
        user.FailedLoginAttempts.Should().Be(0);
    }

    [Fact]
    public async Task An_unknown_address_still_performs_a_hash_verification()
    {
        _users.FindForAuthenticationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((User?)null);

        _passwordHasher.Hash(Arg.Any<string>()).Returns("decoy-hash");
        _passwordHasher.Verify(Arg.Any<string>(), Arg.Any<string>()).Returns((false, false));

        var act = async () => await CreateService()
            .LoginAsync(new LoginRequest("nobody@acme.test", "secret"));

        await act.Should().ThrowAsync<AuthenticationException>();

        // Without this the endpoint returns measurably faster for addresses that do not exist,
        // which is a user-enumeration oracle.
        _passwordHasher.Received(1).Verify("secret", "decoy-hash");
    }

    [Fact]
    public async Task A_wrong_password_increments_the_failure_count()
    {
        var user = ActiveUser();

        _users.FindForAuthenticationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(user);
        _users.GetForUpdateAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _passwordHasher.Verify("wrong", "stored-hash").Returns((false, false));

        var act = async () => await CreateService()
            .LoginAsync(new LoginRequest("operator@acme.test", "wrong"));

        await act.Should().ThrowAsync<AuthenticationException>();

        user.FailedLoginAttempts.Should().Be(1);
        user.LockoutEndsOn.Should().BeNull();
    }

    [Fact]
    public async Task Reaching_the_failure_threshold_locks_the_account()
    {
        var user = ActiveUser();
        user.FailedLoginAttempts = _policy.MaxFailedLoginAttempts - 1;

        _users.FindForAuthenticationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(user);
        _users.GetForUpdateAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _passwordHasher.Verify("wrong", "stored-hash").Returns((false, false));

        var act = async () => await CreateService()
            .LoginAsync(new LoginRequest("operator@acme.test", "wrong"));

        await act.Should().ThrowAsync<AuthenticationException>();

        user.LockoutEndsOn.Should().Be(Now.AddMinutes(_policy.LockoutDurationMinutes));
    }

    [Fact]
    public async Task A_locked_account_is_refused_before_the_password_is_checked()
    {
        var user = ActiveUser();
        user.LockoutEndsOn = Now.AddMinutes(5);

        _users.FindForAuthenticationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(user);

        var act = async () => await CreateService()
            .LoginAsync(new LoginRequest("operator@acme.test", "secret"));

        await act.Should().ThrowAsync<AuthenticationException>();

        _passwordHasher.DidNotReceive().Verify("secret", "stored-hash");
    }

    [Fact]
    public async Task A_suspended_tenant_blocks_sign_in_even_with_correct_credentials()
    {
        var user = ActiveUser();
        user.Tenant!.Status = TenantStatus.Suspended;

        _users.FindForAuthenticationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(user);
        _passwordHasher.Verify("secret", "stored-hash").Returns((true, false));

        var act = async () => await CreateService()
            .LoginAsync(new LoginRequest("operator@acme.test", "secret"));

        await act.Should().ThrowAsync<AuthenticationException>();
    }

    [Fact]
    public async Task A_stale_password_hash_is_upgraded_during_a_successful_sign_in()
    {
        var user = ActiveUser();

        _users.FindForAuthenticationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(user);
        _passwordHasher.Verify("secret", "stored-hash").Returns((true, true));
        _passwordHasher.Hash("secret").Returns("upgraded-hash");

        await CreateService().LoginAsync(
            new LoginRequest("operator@acme.test", "secret"),
            TestContext.Current.CancellationToken);

        // Sign-in is the only moment the plaintext exists, so it is the only moment a work-factor
        // increase can be applied without forcing a password reset.
        user.PasswordHash.Should().Be("upgraded-hash");
    }

    [Fact]
    public async Task Replaying_an_already_rotated_refresh_token_revokes_every_session()
    {
        _refreshTokens.FindByHashAsync("presented-hash", Arg.Any<CancellationToken>())
            .Returns(new RefreshToken
            {
                UserId = UserId,
                TenantId = TenantId,
                SessionId = Guid.NewGuid(),
                TokenHash = "presented-hash",
                ExpiresOn = Now.AddDays(7),
                ConsumedOn = Now.AddMinutes(-5),
            });

        _tokenService.HashRefreshToken("presented").Returns("presented-hash");

        var act = async () => await CreateService()
            .RefreshAsync(new RefreshTokenRequest("presented"));

        await act.Should().ThrowAsync<AuthenticationException>();

        // Refusing only this request would leave the thief's copy usable. The whole chain goes.
        await _refreshTokens.Received(1).RevokeAllForUserAsync(
            UserId,
            Arg.Any<string>(),
            Now,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refresh_token_issued_before_a_security_stamp_change_is_rejected()
    {
        var user = ActiveUser();

        _tokenService.HashRefreshToken("presented").Returns("presented-hash");

        _refreshTokens.FindByHashAsync("presented-hash", Arg.Any<CancellationToken>())
            .Returns(new RefreshToken
            {
                UserId = UserId,
                TenantId = TenantId,
                SessionId = Guid.NewGuid(),
                TokenHash = "presented-hash",
                ExpiresOn = Now.AddDays(7),
                SecurityStamp = Guid.NewGuid(), // Differs from the user's current stamp.
            });

        _users.FindWithRolesAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var act = async () => await CreateService()
            .RefreshAsync(new RefreshTokenRequest("presented"));

        await act.Should().ThrowAsync<AuthenticationException>();
    }

    [Fact]
    public async Task A_successful_refresh_rotates_the_token_and_keeps_the_session()
    {
        var user = ActiveUser();
        var sessionId = Guid.Parse("44444444-4444-4444-4444-444444444444");

        var stored = new RefreshToken
        {
            UserId = UserId,
            TenantId = TenantId,
            SessionId = sessionId,
            TokenHash = "presented-hash",
            ExpiresOn = Now.AddDays(7),
            SecurityStamp = user.SecurityStamp,
        };

        _tokenService.HashRefreshToken("presented").Returns("presented-hash");
        _tokenService.HashRefreshToken("refresh-token").Returns("rotated-hash");
        _refreshTokens.FindByHashAsync("presented-hash", Arg.Any<CancellationToken>()).Returns(stored);
        _users.FindWithRolesAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var response = await CreateService().RefreshAsync(
            new RefreshTokenRequest("presented"),
            TestContext.Current.CancellationToken);

        response.RefreshToken.Should().Be("refresh-token");

        stored.ConsumedOn.Should().Be(Now);
        stored.ReplacedByTokenHash.Should().Be("rotated-hash");

        // The replacement belongs to the same session, so concurrent-session accounting and the
        // sid claim stay stable across a refresh.
        _refreshTokens.Received(1).Add(Arg.Is<RefreshToken>(token => token!.SessionId == sessionId));
    }

    [Fact]
    public async Task Signing_out_everywhere_rotates_the_security_stamp_and_revokes_sessions()
    {
        var user = ActiveUser();
        var originalStamp = user.SecurityStamp;

        _currentUser.UserId = UserId;
        _users.GetForUpdateAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        await CreateService().LogoutEverywhereAsync(TestContext.Current.CancellationToken);

        user.SecurityStamp.Should().NotBe(originalStamp);

        await _refreshTokens.Received(1).RevokeAllForUserAsync(
            UserId,
            Arg.Any<string>(),
            Now,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Signing_out_without_a_session_claim_succeeds_without_disclosing_anything()
    {

        _currentUser.SessionId = null;

        var act = async () => await CreateService().LogoutAsync(TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
    // ---------------------------------------------------------------------------------------
    // Invitation, reset and change password
    // ---------------------------------------------------------------------------------------

    private UserToken StageToken(UserTokenPurpose purpose, User user, DateTimeOffset? expiresOn = null)
    {
        var token = new UserToken
        {
            Id = 4101,
            TenantId = user.TenantId,
            UserId = user.Id,
            Purpose = purpose,
            TokenHash = "presented-hash",
            ExpiresOn = expiresOn ?? Now.AddHours(1),
        };

        _tokenService.HashSecureToken("presented").Returns("presented-hash");

        // Redemption resolves the token by hash with the tenant filter bypassed, because the
        // caller is anonymous at that point and the token belongs to an organisation.
        _userTokens.FindByHashAsync("presented-hash", Arg.Any<CancellationToken>()).Returns(token);
        _users.FindWithRolesAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);

        return token;
    }

    [Fact]
    public async Task Accepting_an_invitation_activates_the_account_and_signs_the_user_in()
    {
        var user = ActiveUser();
        user.Status = UserStatus.Invited;
        user.EmailConfirmed = false;

        var token = StageToken(UserTokenPurpose.Invitation, user);
        _passwordHasher.Hash("correct horse battery 9").Returns("new-hash");

        var tokens = await CreateService().AcceptInvitationAsync(
            new AcceptInvitationRequest("presented", "correct horse battery 9"),
            TestContext.Current.CancellationToken);

        tokens.AccessToken.Should().Be("access-token");

        user.Status.Should().Be(UserStatus.Active);
        user.PasswordHash.Should().Be("new-hash");

        // Redeeming a token sent to the address proves control of it, so no separate verification
        // step is demanded.
        user.EmailConfirmed.Should().BeTrue();

        // Single use: the token is spent, so a forwarded invitation link cannot activate twice.
        token.ConsumedOn.Should().Be(Now);
    }

    [Fact]
    public async Task An_invitation_token_cannot_be_used_for_a_password_reset()
    {
        var user = ActiveUser();
        StageToken(UserTokenPurpose.Invitation, user);

        // Purpose is checked, so a long-lived invitation link cannot be repurposed as a reset -
        // which would otherwise be a 72-hour window to take over an account.
        var act = async () => await CreateService().ResetPasswordAsync(
            new ResetPasswordRequest("presented", "correct horse battery 9"),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<AuthenticationException>();
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var user = ActiveUser();
        StageToken(UserTokenPurpose.PasswordReset, user, expiresOn: Now.AddMinutes(-1));

        var act = async () => await CreateService().ResetPasswordAsync(
            new ResetPasswordRequest("presented", "correct horse battery 9"),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<AuthenticationException>();
    }

    [Fact]
    public async Task An_already_used_token_is_refused()
    {
        var user = ActiveUser();
        var token = StageToken(UserTokenPurpose.PasswordReset, user);
        token.ConsumedOn = Now.AddMinutes(-5);

        var act = async () => await CreateService().ResetPasswordAsync(
            new ResetPasswordRequest("presented", "correct horse battery 9"),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<AuthenticationException>();
    }

    [Fact]
    public async Task A_weak_password_is_refused_before_the_token_is_spent()
    {
        var user = ActiveUser();
        var token = StageToken(UserTokenPurpose.PasswordReset, user);

        var act = async () => await CreateService().ResetPasswordAsync(
            new ResetPasswordRequest("presented", "abc"),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ValidationException>();

        // The token survives a rejected password, so the user can retry with the same link rather
        // than having to request a new one.
        token.ConsumedOn.Should().BeNull();
    }

    [Fact]
    public async Task Resetting_a_password_revokes_every_session_and_clears_a_lockout()
    {
        var user = ActiveUser();
        user.LockoutEndsOn = Now.AddMinutes(10);
        user.FailedLoginAttempts = 3;

        var originalStamp = user.SecurityStamp;
        StageToken(UserTokenPurpose.PasswordReset, user);
        _passwordHasher.Hash(Arg.Any<string>()).Returns("new-hash");

        await CreateService().ResetPasswordAsync(
            new ResetPasswordRequest("presented", "correct horse battery 9"),
            TestContext.Current.CancellationToken);

        user.PasswordHash.Should().Be("new-hash");
        user.SecurityStamp.Should().NotBe(originalStamp);

        // Proving control of the address is stronger evidence than waiting out a lockout timer.
        user.LockoutEndsOn.Should().BeNull();
        user.FailedLoginAttempts.Should().Be(0);

        // Leaving an attacker's session alive would make the reset cosmetic.
        await _refreshTokens.Received(1).RevokeAllForUserAsync(
            user.Id, Arg.Any<string>(), Now, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Changing_a_password_requires_the_current_one()
    {
        var user = ActiveUser();
        _currentUser.UserId = UserId;
        _users.GetForUpdateAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _passwordHasher.Verify("wrong", "stored-hash").Returns((false, false));

        var act = async () => await CreateService().ChangePasswordAsync(
            new ChangePasswordRequest("wrong", "correct horse battery 9"),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Changing_a_password_keeps_the_current_session_and_ends_the_others()
    {
        var user = ActiveUser();
        var currentSession = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var otherSession = Guid.Parse("66666666-6666-6666-6666-666666666666");

        _currentUser.UserId = UserId;
        _currentUser.SessionId = currentSession;
        _users.GetForUpdateAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _passwordHasher.Verify("stored", "stored-hash").Returns((true, false));
        _passwordHasher.Hash(Arg.Any<string>()).Returns("new-hash");

        var mine = new RefreshToken { UserId = UserId, SessionId = currentSession, TokenHash = "a", ExpiresOn = Now.AddDays(1) };
        var theirs = new RefreshToken { UserId = UserId, SessionId = otherSession, TokenHash = "b", ExpiresOn = Now.AddDays(1) };

        _refreshTokens.GetActiveSessionsAsync(UserId, Now, Arg.Any<CancellationToken>())
            .Returns([mine, theirs]);

        await CreateService().ChangePasswordAsync(
            new ChangePasswordRequest("stored", "correct horse battery 9"),
            TestContext.Current.CancellationToken);

        // The caller keeps working: signing someone out of the tab they just used to change their
        // password is hostile, so their session is re-stamped rather than revoked.
        mine.RevokedOn.Should().BeNull();
        mine.SecurityStamp.Should().Be(user.SecurityStamp);

        theirs.RevokedOn.Should().Be(Now);
    }

    [Fact]
    public async Task Forgot_password_does_not_send_a_link_to_a_disabled_account()
    {
        var user = ActiveUser();
        user.Status = UserStatus.Disabled;

        _users.FindForAuthenticationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(user);

        await CreateService().ForgotPasswordAsync(
            new ForgotPasswordRequest("operator@acme.test"),
            TestContext.Current.CancellationToken);

        // Otherwise a dismissed employee could restore their own access with a reset link.
        await _activation.DidNotReceive().SendPasswordResetAsync(
            Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Forgot_password_reports_success_for_an_unknown_address()
    {
        _users.FindForAuthenticationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var act = async () => await CreateService().ForgotPasswordAsync(
            new ForgotPasswordRequest("nobody@acme.test"),
            TestContext.Current.CancellationToken);

        // Reporting otherwise would turn the endpoint into an account-enumeration oracle.
        await act.Should().NotThrowAsync();
    }
}
