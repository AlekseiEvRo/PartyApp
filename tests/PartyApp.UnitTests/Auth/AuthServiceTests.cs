using System.IdentityModel.Tokens.Jwt;

using FluentAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

using PartyApp.Api.Modules.Auth.Requests;
using PartyApp.Api.Modules.Auth.Responces;
using PartyApp.Api.Modules.Auth.Services;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Auth;

public class AuthServiceTests : IDisposable
{
    private const string SigningKey = "TestSigningKeyThatIsLongEnoughForHmacSha256_42";

    private readonly SqliteTestHost _host;
    private readonly AuthService _service;
    private readonly PasswordHasher<User> _passwordHasher = new();

    public AuthServiceTests() : this(welcomeBonus: 100)
    {
    }

    private AuthServiceTests(int welcomeBonus)
    {
        _host = new SqliteTestHost();

        IConfigurationRootAccessor accessor = new(welcomeBonus);
        _service = new AuthService(
            _host.Db,
            _passwordHasher,
            new TokenService(accessor.Configuration),
            accessor.Configuration);
    }

    public void Dispose()
    {
        _host.Dispose();
    }

    private static RegisterRequest Register(string username = "alice", string displayName = "Алиса", string password = "secret123")
    {
        return new RegisterRequest(username, displayName, password);
    }

    [Fact]
    public async Task RegisterAsync_PersistsPlayerWithHashedPassword()
    {
        AuthResponse response = await _service.RegisterAsync(Register(password: "secret123"));

        User user = await _host.Db.Users.SingleAsync();
        user.Id.Should().Be(response.UserId);
        user.Role.Should().Be(UserRole.Player);
        user.PasswordHash.Should().NotBe("secret123");
        _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, "secret123")
            .Should().Be(PasswordVerificationResult.Success);
    }

    [Theory]
    [InlineData("  Alice  ", "alice")]
    [InlineData("BOB", "bob")]
    [InlineData("a", "a")]
    public async Task RegisterAsync_NormalizesUsername(string raw, string expected)
    {
        AuthResponse response = await _service.RegisterAsync(Register(username: raw));

        response.Username.Should().Be(expected);
        (await _host.Db.Users.SingleAsync()).Username.Should().Be(expected);
    }

    [Fact]
    public async Task RegisterAsync_TrimsDisplayName()
    {
        AuthResponse response = await _service.RegisterAsync(Register(displayName: "  Алиса  "));

        response.DisplayName.Should().Be("Алиса");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RegisterAsync_WithBlankUsername_Throws(string username)
    {
        Func<Task> act = () => _service.RegisterAsync(Register(username: username));

        await act.Should().ThrowAsync<AuthException>()
            .WithMessage("Имя пользователя не может быть пустым");
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    public async Task RegisterAsync_WithTooShortPassword_Throws(string password)
    {
        Func<Task> act = () => _service.RegisterAsync(Register(password: password));

        await act.Should().ThrowAsync<AuthException>()
            .WithMessage("Пароль должен содержать минимум 6 символов");
    }

    [Fact]
    public async Task RegisterAsync_WithExactlySixCharPassword_Succeeds()
    {
        Func<Task> act = () => _service.RegisterAsync(Register(password: "123456"));

        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("ALICE")]
    [InlineData("  Alice  ")]
    public async Task RegisterAsync_WithDuplicateUsername_Throws(string duplicate)
    {
        await _service.RegisterAsync(Register(username: "alice"));

        Func<Task> act = () => _service.RegisterAsync(Register(username: duplicate));

        await act.Should().ThrowAsync<AuthException>()
            .WithMessage("Пользователь с таким именем уже существует");
    }

    [Fact]
    public async Task RegisterAsync_CreatesWalletWithWelcomeBonusAndTransaction()
    {
        AuthResponse response = await _service.RegisterAsync(Register());

        Wallet wallet = await _host.Db.Wallets.SingleAsync(w => w.UserId == response.UserId);
        wallet.Balance.Should().Be(100);

        WalletTransaction transaction = await _host.Db.WalletTransactions.SingleAsync();
        transaction.WalletId.Should().Be(wallet.Id);
        transaction.Amount.Should().Be(100);
        transaction.Type.Should().Be(WalletTransactionType.AdminGrant);
        transaction.Description.Should().Be("Приветственный бонус");
    }

    [Fact]
    public async Task RegisterAsync_WithoutWelcomeBonus_CreatesEmptyWalletWithoutTransaction()
    {
        using SqliteTestHost host = new();
        IConfigurationRoot configuration = TestConfiguration.Create(
            ("Jwt:SigningKey", SigningKey),
            ("Party:WelcomeBonus", "0"));

        AuthService service = new(
            host.Db,
            _passwordHasher,
            new TokenService(configuration),
            configuration);

        AuthResponse response = await service.RegisterAsync(Register());

        Wallet wallet = await host.Db.Wallets.SingleAsync(w => w.UserId == response.UserId);
        wallet.Balance.Should().Be(0);
        (await host.Db.WalletTransactions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RegisterAsync_ReturnsTokenWithUserClaims()
    {
        AuthResponse response = await _service.RegisterAsync(Register(username: "alice", displayName: "Алиса"));

        response.AccessToken.Should().NotBeNullOrWhiteSpace();
        response.ExpiresAtUtc.Should().BeAfter(DateTime.UtcNow);

        JwtSecurityToken jwt = new JwtSecurityTokenHandler().ReadJwtToken(response.AccessToken);
        jwt.Claims.Single(c => c.Type == "sub").Value.Should().Be(response.UserId.ToString());
        jwt.Claims.Single(c => c.Type == "name").Value.Should().Be("alice");
        jwt.Claims.Single(c => c.Type == "displayName").Value.Should().Be("Алиса");
        jwt.Claims.Single(c => c.Type == "role").Value.Should().Be("Player");
        jwt.Claims.Single(c => c.Type == "stamp").Value.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task LoginAsync_WithValidCredentials_ReturnsTokenAndProfile()
    {
        AuthResponse registered = await _service.RegisterAsync(Register());

        AuthResponse response = await _service.LoginAsync(new LoginRequest("alice", "secret123"));

        response.UserId.Should().Be(registered.UserId);
        response.Username.Should().Be("alice");
        response.DisplayName.Should().Be("Алиса");
        response.Role.Should().Be("Player");
        response.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task LoginAsync_NormalizesUsername()
    {
        await _service.RegisterAsync(Register(username: "alice"));

        AuthResponse response = await _service.LoginAsync(new LoginRequest("  ALICE  ", "secret123"));

        response.Username.Should().Be("alice");
    }

    [Fact]
    public async Task LoginAsync_WithUnknownUser_ThrowsGenericError()
    {
        Func<Task> act = () => _service.LoginAsync(new LoginRequest("nobody", "secret123"));

        await act.Should().ThrowAsync<AuthException>()
            .WithMessage("Неверный логин или пароль");
    }

    [Fact]
    public async Task LoginAsync_WithWrongPassword_ThrowsSameGenericError()
    {
        await _service.RegisterAsync(Register());

        Func<Task> act = () => _service.LoginAsync(new LoginRequest("alice", "wrong-password"));

        await act.Should().ThrowAsync<AuthException>()
            .WithMessage("Неверный логин или пароль");
    }

    [Fact]
    public async Task LoginAsync_WithInactiveUser_Throws()
    {
        await _service.RegisterAsync(Register());

        User user = await _host.Db.Users.SingleAsync();
        user.IsActive = false;
        await _host.Db.SaveChangesAsync();

        Func<Task> act = () => _service.LoginAsync(new LoginRequest("alice", "secret123"));

        await act.Should().ThrowAsync<AuthException>()
            .WithMessage("Аккаунт заблокирован администратором");
    }

    [Fact]
    public async Task LoginAsync_ForAdminUser_ReturnsAdminRole()
    {
        User admin = TestData.User("admin", UserRole.Admin, "Админ");
        admin.PasswordHash = _passwordHasher.HashPassword(admin, "admin123");
        _host.Db.Users.Add(admin);
        await _host.Db.SaveChangesAsync();

        AuthResponse response = await _service.LoginAsync(new LoginRequest("admin", "admin123"));

        response.Role.Should().Be("Admin");
    }

    /// <summary>Конфиг JWT + бонус в одном месте, чтобы тесты не дублировали словарь.</summary>
    private sealed class IConfigurationRootAccessor
    {
        public IConfigurationRootAccessor(int welcomeBonus)
        {
            Configuration = TestConfiguration.Create(
                ("Jwt:SigningKey", SigningKey),
                ("Jwt:Issuer", "test-issuer"),
                ("Jwt:Audience", "test-audience"),
                ("Party:WelcomeBonus", welcomeBonus.ToString()));
        }

        public IConfigurationRoot Configuration { get; }
    }
}