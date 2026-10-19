using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Nexora.Application.Abstractions;
using Nexora.Application.Identity;
using Nexora.Application.Security;
using Nexora.Application.UseCases.Auth;
using Nexora.Contracts.Auth;
using Nexora.Domain.Common;
using Nexora.Domain.Identity;

namespace Nexora.UnitTests.Auth;

public sealed class LoginHandlerTests
{
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IPermissionService _permissions = Substitute.For<IPermissionService>();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly ITokenService _tokens = Substitute.For<ITokenService>();
    private readonly IMfaService _mfa = Substitute.For<IMfaService>();
    private readonly ISecurityEventLog _events = Substitute.For<ISecurityEventLog>();
    private readonly IDateTimeProvider _clock = Substitute.For<IDateTimeProvider>();

    private LoginHandler CreateSut()
    {
        var issuer = new AccessTokenIssuer(_permissions, _tokens, _users, _clock);
        return new LoginHandler(_users, _permissions, _hasher, _mfa, _events, issuer, _clock, NullLogger<LoginHandler>.Instance);
    }

    public LoginHandlerTests() => _clock.UtcNow.Returns(DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Unknown_user_yields_generic_unauthorized()
    {
        _users.FindByEmailAsync("ghost@x", Arg.Any<CancellationToken>()).Returns((AppUser?)null);

        var result = await CreateSut().HandleAsync(new LoginRequest("ghost@x", "pw", null));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKind.Unauthorized);
        result.Error.Code.Should().Be("auth.invalid_credentials");
    }

    [Fact]
    public async Task Unknown_user_costs_a_password_check_like_a_known_one()
    {
        // Audit 2026-10, A-011 : sans vérification, la réponse immédiate révélait que le compte n'existe pas.
        _users.FindByEmailAsync("ghost@x", Arg.Any<CancellationToken>()).Returns((AppUser?)null);

        await CreateSut().HandleAsync(new LoginRequest("ghost@x", "pw", null));

        _hasher.Received(1).Verify("pw", Arg.Any<string>());
    }

    [Fact]
    public async Task Wrong_password_yields_generic_unauthorized_and_registers_the_failure()
    {
        var user = new AppUser { Id = Guid.NewGuid(), Email = "u@x", PasswordHash = "hash", Status = "active" };
        _users.FindByEmailAsync("u@x", Arg.Any<CancellationToken>()).Returns(user);
        _hasher.Verify("bad", "hash").Returns(false);

        var result = await CreateSut().HandleAsync(new LoginRequest("u@x", "bad", null));

        result.Error!.Code.Should().Be("auth.invalid_credentials");
        await _users.Received(1).RegisterFailedLoginAsync(user.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Locked_account_is_refused_without_checking_the_password()
    {
        var user = new AppUser
        {
            Id = Guid.NewGuid(), Email = "u@x", PasswordHash = "hash", Status = "active",
            LockedUntil = DateTimeOffset.UnixEpoch.AddMinutes(10),
        };
        _users.FindByEmailAsync("u@x", Arg.Any<CancellationToken>()).Returns(user);

        var result = await CreateSut().HandleAsync(new LoginRequest("u@x", "whatever", null));

        // Audit 2026-10 éd. 2 : même réponse et même calcul qu'un mot de passe faux — le verrouillage ne désigne plus
        // un compte existant.
        result.Error!.Code.Should().Be("auth.invalid_credentials");
        _hasher.Received(1).Verify("whatever", "hash");
    }

    [Fact]
    public async Task Requesting_unauthorized_company_is_forbidden()
    {
        var user = new AppUser { Id = Guid.NewGuid(), Email = "u@x", PasswordHash = "hash", Status = "active" };
        _users.FindByEmailAsync("u@x", Arg.Any<CancellationToken>()).Returns(user);
        _hasher.Verify("good", "hash").Returns(true);
        _permissions.LoadProfileAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(new UserSecurityProfile(user.Id, []));

        var result = await CreateSut().HandleAsync(new LoginRequest("u@x", "good", Guid.NewGuid()));

        result.Error!.Kind.Should().Be(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Mfa_enabled_user_gets_a_challenge_instead_of_a_token()
    {
        var user = new AppUser
        {
            Id = Guid.NewGuid(), Email = "u@x", PasswordHash = "hash", Status = "active",
            MfaEnabled = true, MfaSecret = "JBSWY3DPEHPK3PXP",
        };
        _users.FindByEmailAsync("u@x", Arg.Any<CancellationToken>()).Returns(user);
        _hasher.Verify("good", "hash").Returns(true);
        _permissions.LoadProfileAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(new UserSecurityProfile(user.Id, []));
        _mfa.IssueChallengeAsync(user.Id, null, Arg.Any<CancellationToken>())
            .Returns(("challenge-token", DateTimeOffset.UnixEpoch.AddMinutes(5)));

        var result = await CreateSut().HandleAsync(new LoginRequest("u@x", "good", null));

        result.IsSuccess.Should().BeTrue();
        result.Value!.MfaRequired.Should().BeTrue();
        result.Value.MfaToken.Should().Be("challenge-token");
        result.Value.AccessToken.Should().BeEmpty();
    }

    [Fact]
    public async Task Valid_credentials_issue_token()
    {
        var user = new AppUser { Id = Guid.NewGuid(), Email = "u@x", PasswordHash = "hash", Status = "active" };
        var companyId = Guid.NewGuid();
        _users.FindByEmailAsync("u@x", Arg.Any<CancellationToken>()).Returns(user);
        _hasher.Verify("good", "hash").Returns(true);
        _permissions.LoadProfileAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(new UserSecurityProfile(user.Id,
                [new CompanyMembership(companyId, "ACME", null, ["manager"], ["sales.view"])]));
        _tokens.IssueAccessToken(user, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid?>())
            .Returns(new IssuedToken("jwt", DateTimeOffset.UnixEpoch.AddHours(1)));

        var result = await CreateSut().HandleAsync(new LoginRequest("u@x", "good", companyId));

        result.IsSuccess.Should().BeTrue();
        result.Value!.AccessToken.Should().Be("jwt");
        result.Value.User.Permissions.Should().Contain("sales.view");
        await _users.Received(1).TouchLastLoginAsync(user.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }
}
