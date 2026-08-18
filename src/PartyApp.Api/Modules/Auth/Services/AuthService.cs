using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PartyApp.Api.Modules.Auth.Requests;
using PartyApp.Api.Modules.Auth.Responces;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Auth.Services;

public class AuthService : IAuthService
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasher<User> _passwordHasher;
    private readonly ITokenService _tokenService;
    private readonly IConfiguration _configuration;

    public AuthService(
        AppDbContext db,
        IPasswordHasher<User> passwordHasher,
        ITokenService tokenService,
        IConfiguration configuration)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _configuration = configuration;
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        var username = request.Username.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(username))
            throw new AuthException("Имя пользователя не может быть пустым");

        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 6)
            throw new AuthException("Пароль должен содержать минимум 6 символов");

        var exists = await _db.Users.AnyAsync(u => u.Username == username, ct);
        if (exists)
            throw new AuthException("Пользователь с таким именем уже существует");

        var user = new User
        {
            Username = username,
            DisplayName = request.DisplayName.Trim(),
            Role = UserRole.Player
        };

        user.PasswordHash = _passwordHasher.HashPassword(user, request.Password);

        // Создаём кошелёк и приветственный бонус
        var welcomeBonus = int.Parse(_configuration["Party:WelcomeBonus"] ?? "0");
        var wallet = new Domain.Entities.Wallet
        {
            UserId = user.Id,
            Balance = welcomeBonus
        };
        user.Wallet = wallet;

        if (welcomeBonus > 0)
        {
            wallet.Transactions.Add(new WalletTransaction
            {
                WalletId = wallet.Id,
                Amount = welcomeBonus,
                Type = WalletTransactionType.AdminGrant,
                Description = "Приветственный бонус"
            });
        }

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        return BuildAuthResponse(user);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var username = request.Username.Trim().ToLowerInvariant();

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
        if (user is null)
            throw new AuthException("Неверный логин или пароль");

        var result = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
            throw new AuthException("Неверный логин или пароль");

        return BuildAuthResponse(user);
    }

    private AuthResponse BuildAuthResponse(User user)
    {
        var (token, expiresAtUtc) = _tokenService.GenerateToken(
            user.Id,
            user.Username,
            user.DisplayName,
            user.Role.ToString());

        return new AuthResponse(
            token,
            expiresAtUtc,
            user.Id,
            user.Username,
            user.DisplayName,
            user.Role.ToString());
    }
}