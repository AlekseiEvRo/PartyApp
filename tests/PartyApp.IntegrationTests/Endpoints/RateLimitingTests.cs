using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

/// <summary>
/// В общей тестовой фабрике лимиты выключены, поэтому здесь поднимается
/// отдельное приложение с маленьким окном для /api/auth.
/// </summary>
public class RateLimitingTests
{
    [Fact]
    public async Task AuthEndpoints_WhenLimitExceeded_ReturnTooManyRequests()
    {
        using PartyAppFactory factory = new()
        {
            ConfigureOverrides = settings =>
            {
                settings["RateLimiting:Enabled"] = "true";
                settings["RateLimiting:AuthPermitLimit"] = "2";
                settings["RateLimiting:AuthWindowSeconds"] = "60";
            }
        };
        using HttpClient client = factory.CreateClient();

        string username = PartyAppApi.UniqueUsername();

        // Первый запрос — регистрация — проходят
        HttpResponseMessage register = await client.PostAsJsonAsync(
            "/api/auth/register",
            new { username, displayName = "Лимит", password = "secret123" });
        register.StatusCode.Should().Be(HttpStatusCode.OK);

        // Второй — логин — тоже
        HttpResponseMessage login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { username, password = "secret123" });
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        // Третий упирается в лимит
        HttpResponseMessage third = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { username, password = "secret123" });
        third.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        JsonElement error = await PartyAppApi.ReadJsonAsync(third);
        error.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task AuthEndpoints_WhenLimitsDisabled_DoNotLimit()
    {
        using PartyAppFactory factory = new()
        {
            ConfigureOverrides = settings =>
            {
                settings["RateLimiting:Enabled"] = "false";
                settings["RateLimiting:AuthPermitLimit"] = "1";
                settings["RateLimiting:AuthWindowSeconds"] = "60";
            }
        };
        using HttpClient client = factory.CreateClient();

        string username = PartyAppApi.UniqueUsername();
        HttpResponseMessage first = await client.PostAsJsonAsync(
            "/api/auth/register",
            new { username, displayName = "Без лимита", password = "secret123" });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        HttpResponseMessage second = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { username, password = "secret123" });
        second.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}