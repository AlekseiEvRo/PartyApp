using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using PartyApp.Domain.Entities;
using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

/// <summary>Управление игроками из админки: роли, блокировка, кик, сброс пароля и журнал действий.</summary>
public class AdminPlayerManagementTests : IClassFixture<PartyAppFactory>
{
    private readonly PartyAppFactory _factory;
    private readonly PartyAppApi _api;

    public AdminPlayerManagementTests(PartyAppFactory factory)
    {
        _factory = factory;
        _api = new PartyAppApi(factory);
    }

    private Task<HttpResponseMessage> PatchAsync(Guid playerId, object body)
    {
        return _api.Client.PatchAsJsonAsync($"/api/admin/players/{playerId}", body);
    }

    [Fact]
    public async Task UpdatePlayer_AsPlayer_ReturnsForbidden()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage response = await PatchAsync(player.Id, new { displayName = "Взлом" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UpdatePlayer_WithoutChanges_ReturnsBadRequest()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await PatchAsync(player.Id, new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("error").GetString()
            .Should().Be("Нечего обновлять");
    }

    [Fact]
    public async Task UpdatePlayer_RenamesPlayerAndWritesAudit()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await PatchAsync(player.Id, new { displayName = "Новое имя" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement json = await PartyAppApi.ReadJsonAsync(response);
        json.GetProperty("displayName").GetString().Should().Be("Новое имя");
        json.GetProperty("isActive").GetBoolean().Should().BeTrue();

        await _factory.DbAsync(async db =>
        {
            AdminAuditLog log = await db.AdminAuditLogs
                .Where(a => a.Action == "rename" && a.TargetUserId == player.Id)
                .OrderByDescending(a => a.CreatedAt)
                .FirstAsync();
            log.AdminId.Should().Be(admin.Id);
            log.Details.Should().Be($"{player.DisplayName} → Новое имя");
        });
    }

    [Fact]
    public async Task UpdatePlayer_WithBlankName_ReturnsBadRequest()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await PatchAsync(player.Id, new { displayName = "   " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdatePlayer_WithUnknownRole_ReturnsBadRequest()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await PatchAsync(player.Id, new { role = "Wizard" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("error").GetString()
            .Should().Be("Неизвестная роль");
    }

    [Fact]
    public async Task UpdatePlayer_ChangesRoleAndRevokesOldTokens()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await PatchAsync(player.Id, new { role = "Admin" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Старый токен выдан с прежней ролью и больше не действует
        _api.Authorize(player);
        HttpResponseMessage me = await _api.Client.GetAsync("/api/auth/me");
        me.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        TestUser relogged = await _api.LoginAsync(player.Username);
        relogged.Role.Should().Be("Admin");

        _api.Authorize(relogged);
        (await _api.Client.GetAsync("/api/admin/players")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdatePlayer_SelfRoleAndBan_ReturnBadRequest()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        (await PatchAsync(admin.Id, new { role = "Player" })).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await PatchAsync(admin.Id, new { isActive = false })).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdatePlayer_UnknownPlayer_ReturnsNotFound()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await PatchAsync(Guid.NewGuid(), new { displayName = "Никто" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdatePlayer_BansAndUnbans()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(admin);

        HttpResponseMessage ban = await PatchAsync(player.Id, new { isActive = false });
        ban.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(ban)).GetProperty("isActive").GetBoolean().Should().BeFalse();

        // Заблокированный не может войти, а его старый токен отклоняется
        (await _api.LoginRawAsync(player.Username)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _api.Authorize(player);
        (await _api.Client.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        _api.Authorize(admin);
        HttpResponseMessage unban = await PatchAsync(player.Id, new { isActive = true });
        unban.StatusCode.Should().Be(HttpStatusCode.OK);

        (await _api.LoginRawAsync(player.Username)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Kick_RevokesTokensButKeepsPassword()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(admin);

        HttpResponseMessage kick = await _api.Client.PostAsync($"/api/admin/players/{player.Id}/kick", null);
        kick.StatusCode.Should().Be(HttpStatusCode.OK);

        _api.Authorize(player);
        (await _api.Client.GetAsync("/api/wallet/balance")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        // Пароль не менялся — игрок может войти заново
        TestUser again = await _api.LoginAsync(player.Username);
        again.Id.Should().Be(player.Id);
    }

    [Fact]
    public async Task Kick_Self_ReturnsBadRequest()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await _api.Client.PostAsync($"/api/admin/players/{admin.Id}/kick", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ResetPassword_ChangesPasswordAndRevokesTokens()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await _api.Client.PostAsync(
            $"/api/admin/players/{player.Id}/reset-password", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string password = (await PartyAppApi.ReadJsonAsync(response)).GetProperty("password").GetString()!;
        password.Length.Should().BeGreaterThanOrEqualTo(6);

        (await _api.LoginRawAsync(player.Username, "secret123")).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await _api.LoginRawAsync(player.Username, password)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ResetPassword_Self_ReturnsBadRequest()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await _api.Client.PostAsync(
            $"/api/admin/players/{admin.Id}/reset-password", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Players_IncludeIsActiveFlag()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await _api.Client.GetAsync("/api/admin/players");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<JsonElement> players = (await PartyAppApi.ReadJsonAsync(response)).EnumerateArray().ToList();
        players.Single(p => p.GetProperty("id").GetGuid() == player.Id)
            .GetProperty("isActive").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Audit_AsPlayer_ReturnsForbidden()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage response = await _api.Client.GetAsync("/api/admin/audit");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Audit_AsAdmin_ReturnsActionsNewestFirst()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(admin);

        (await _api.Client.PostAsJsonAsync(
            "/api/admin/grant-points",
            new { playerId = player.Id, amount = 5, reason = "За тост" })).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await PatchAsync(player.Id, new { displayName = "Переименован" })).StatusCode
            .Should().Be(HttpStatusCode.OK);

        HttpResponseMessage response = await _api.Client.GetAsync("/api/admin/audit");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement json = await PartyAppApi.ReadJsonAsync(response);
        json.GetProperty("total").GetInt32().Should().BeGreaterThanOrEqualTo(2);

        JsonElement newest = json.GetProperty("items")[0];
        newest.GetProperty("action").GetString().Should().Be("rename");
        newest.GetProperty("adminName").GetString().Should().Be(admin.DisplayName);
        newest.GetProperty("targetUserId").GetGuid().Should().Be(player.Id);
        newest.GetProperty("targetName").GetString().Should().Be("Переименован");

        JsonElement oldest = json.GetProperty("items")[1];
        oldest.GetProperty("action").GetString().Should().Be("grant_points");
        oldest.GetProperty("details").GetString().Should().Be("+5 · За тост");
    }
}
