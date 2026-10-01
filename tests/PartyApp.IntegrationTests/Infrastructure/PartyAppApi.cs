using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using PartyApp.Api.Modules.Auth.Requests;
using PartyApp.Api.Modules.Auth.Responces;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;

namespace PartyApp.IntegrationTests.Infrastructure;

/// <summary>Пользователь, зарегистрированный через реальный API, вместе с его JWT.</summary>
public sealed record TestUser(Guid Id, string Username, string DisplayName, string Role, string Token)
{
    public bool IsAdmin => Role == nameof(UserRole.Admin);
}

/// <summary>
/// Обёртка над HttpClient тестового приложения: регистрация, логин, авторизация.
/// </summary>
public sealed class PartyAppApi
{
    private const string DefaultPassword = "secret123";

    private readonly PartyAppFactory _factory;

    public PartyAppApi(PartyAppFactory factory)
    {
        _factory = factory;
        Client = factory.CreateClient();
    }

    public HttpClient Client { get; }

    public static string UniqueUsername(string prefix = "user")
    {
        return $"{prefix}_{Guid.NewGuid():N}";
    }

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public Task<HttpResponseMessage> RegisterRawAsync(
        string username,
        string password = DefaultPassword,
        string? displayName = null)
    {
        return Client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(username, displayName ?? username, password));
    }

    public async Task<TestUser> RegisterAsync(
        string? username = null,
        string password = DefaultPassword,
        string? displayName = null)
    {
        username ??= UniqueUsername();
        HttpResponseMessage response = await RegisterRawAsync(username, password, displayName);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "регистрация {0} должна проходить: {1}",
            username,
            await response.Content.ReadAsStringAsync());

        AuthResponse auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        return ToTestUser(auth);
    }

    public Task<HttpResponseMessage> LoginRawAsync(string username, string password = DefaultPassword)
    {
        return Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, password));
    }

    public async Task<TestUser> LoginAsync(string username, string password = DefaultPassword)
    {
        HttpResponseMessage response = await LoginRawAsync(username, password);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "логин {0} должен проходить: {1}",
            username,
            await response.Content.ReadAsStringAsync());

        AuthResponse auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        return ToTestUser(auth);
    }

    /// <summary>Регистрирует пользователя и, если нужно, меняет роль в БД и логинит заново.</summary>
    public async Task<TestUser> CreateUserAsync(
        string? username = null,
        UserRole role = UserRole.Player,
        string password = DefaultPassword)
    {
        TestUser user = await RegisterAsync(username, password);
        if (role == UserRole.Player)
            return user;

        await _factory.DbAsync(async db =>
        {
            User entity = await db.Users.SingleAsync(u => u.Id == user.Id);
            entity.Role = role;
            await db.SaveChangesAsync();
        });

        return await LoginAsync(user.Username, password);
    }

    public Task<TestUser> CreateAdminAsync(string? username = null)
    {
        return CreateUserAsync(username ?? UniqueUsername("admin"), UserRole.Admin);
    }

    public void Authorize(TestUser user)
    {
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
    }

    public void ClearAuthorization()
    {
        Client.DefaultRequestHeaders.Authorization = null;
    }

    public async Task<int> GetBalanceAsync(TestUser user)
    {
        Authorize(user);
        HttpResponseMessage response = await Client.GetAsync("/api/wallet/balance");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await ReadJsonAsync(response)).GetProperty("balance").GetInt32();
    }

    private static TestUser ToTestUser(AuthResponse auth)
    {
        return new TestUser(auth.UserId, auth.Username, auth.DisplayName, auth.Role, auth.AccessToken);
    }
}