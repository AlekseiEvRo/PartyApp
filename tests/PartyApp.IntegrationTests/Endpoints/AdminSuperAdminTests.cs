using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using PartyApp.Domain.Enums;
using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

/// <summary>
/// Роль супер-админа: назначать может только супер-админ, его роль защищена
/// от всех, остальные действия над ним — только другому супер-админу.
/// </summary>
public class AdminSuperAdminTests : IClassFixture<PartyAppFactory>
{
    private readonly PartyAppApi _api;

    public AdminSuperAdminTests(PartyAppFactory factory)
    {
        _api = new PartyAppApi(factory);
    }

    private Task<TestUser> CreateSuperAdminAsync()
    {
        return _api.CreateUserAsync(PartyAppApi.UniqueUsername("super"), UserRole.SuperAdmin);
    }

    private Task<HttpResponseMessage> PatchAsync(Guid playerId, object body)
    {
        return _api.Client.PatchAsJsonAsync($"/api/admin/players/{playerId}", body);
    }

    [Fact]
    public async Task SuperAdmin_HasAccessToAdminEndpoints()
    {
        TestUser superAdmin = await CreateSuperAdminAsync();
        _api.Authorize(superAdmin);

        HttpResponseMessage response = await _api.Client.GetAsync("/api/admin/players");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SuperAdminRole_CannotBeChangedByAnyone()
    {
        TestUser superAdmin = await CreateSuperAdminAsync();
        TestUser otherSuperAdmin = await CreateSuperAdminAsync();
        TestUser admin = await _api.CreateAdminAsync();

        _api.Authorize(otherSuperAdmin);
        HttpResponseMessage bySuper = await PatchAsync(superAdmin.Id, new { role = "Player" });
        bySuper.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PartyAppApi.ReadJsonAsync(bySuper)).GetProperty("error").GetString()
            .Should().Be("Роль супер-админа изменить нельзя");

        _api.Authorize(admin);
        HttpResponseMessage byAdmin = await PatchAsync(superAdmin.Id, new { role = "Admin" });
        byAdmin.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Роль осталась прежней
        _api.Authorize(superAdmin);
        JsonElement me = await PartyAppApi.ReadJsonAsync(await _api.Client.GetAsync("/api/auth/me"));
        me.GetProperty("role").GetString().Should().Be("SuperAdmin");
    }

    [Fact]
    public async Task AssignSuperAdmin_OnlyBySuperAdmin()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();

        _api.Authorize(admin);
        HttpResponseMessage byAdmin = await PatchAsync(player.Id, new { role = "SuperAdmin" });
        byAdmin.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PartyAppApi.ReadJsonAsync(byAdmin)).GetProperty("error").GetString()
            .Should().Be("Только супер-админ может назначить супер-админа");

        TestUser superAdmin = await CreateSuperAdminAsync();
        _api.Authorize(superAdmin);
        HttpResponseMessage bySuper = await PatchAsync(player.Id, new { role = "SuperAdmin" });
        bySuper.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(bySuper)).GetProperty("role").GetString()
            .Should().Be("SuperAdmin");

        TestUser relogged = await _api.LoginAsync(player.Username);
        relogged.Role.Should().Be("SuperAdmin");
        _api.Authorize(relogged);
        (await _api.Client.GetAsync("/api/admin/players")).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task BanAndRenameSuperAdmin_OnlyBySuperAdmin()
    {
        TestUser superAdmin = await CreateSuperAdminAsync();
        TestUser otherSuperAdmin = await CreateSuperAdminAsync();
        TestUser admin = await _api.CreateAdminAsync();

        _api.Authorize(admin);
        (await PatchAsync(superAdmin.Id, new { isActive = false })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await PatchAsync(superAdmin.Id, new { displayName = "Новое имя" })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

        // Супер-админ может переименовать другого супер-админа
        _api.Authorize(otherSuperAdmin);
        HttpResponseMessage rename = await PatchAsync(superAdmin.Id, new { displayName = "Главный" });
        rename.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(rename)).GetProperty("displayName").GetString()
            .Should().Be("Главный");

        // И забанить/разбанить его
        HttpResponseMessage ban = await PatchAsync(superAdmin.Id, new { isActive = false });
        ban.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(ban)).GetProperty("isActive").GetBoolean().Should().BeFalse();

        HttpResponseMessage unban = await PatchAsync(superAdmin.Id, new { isActive = true });
        unban.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task KickAndResetPasswordSuperAdmin_OnlyBySuperAdmin()
    {
        TestUser superAdmin = await CreateSuperAdminAsync();
        TestUser otherSuperAdmin = await CreateSuperAdminAsync();
        TestUser admin = await _api.CreateAdminAsync();

        _api.Authorize(admin);
        (await _api.Client.PostAsync($"/api/admin/players/{superAdmin.Id}/kick", null)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await _api.Client.PostAsync($"/api/admin/players/{superAdmin.Id}/reset-password", null)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

        _api.Authorize(otherSuperAdmin);
        (await _api.Client.PostAsync($"/api/admin/players/{superAdmin.Id}/kick", null)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await _api.Client.PostAsync($"/api/admin/players/{superAdmin.Id}/reset-password", null)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AdminCanStillManageRegularPlayersAndAdmins()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();

        _api.Authorize(admin);
        (await PatchAsync(player.Id, new { role = "Admin" })).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await PatchAsync(player.Id, new { role = "Player" })).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }
}
