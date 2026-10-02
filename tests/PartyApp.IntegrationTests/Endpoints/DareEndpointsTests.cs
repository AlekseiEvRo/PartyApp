using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

public class DareEndpointsTests : IDisposable
{
    private readonly PartyAppFactory _factory = new();
    private readonly PartyAppApi _api;

    public DareEndpointsTests()
    {
        _api = new PartyAppApi(_factory);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private async Task<(Guid SessionId, TestUser Admin)> StartDareAsync()
    {
        TestUser admin = await _api.CreateAdminAsync();
        var definition = await _factory.DbAsync(db => db.EventDefinitions
            .AsNoTracking()
            .SingleAsync(d => d.Type == "dare"));

        _api.Authorize(admin);
        HttpResponseMessage start = await _api.Client.PostAsync($"/api/events/{definition.Id}/start", null);
        start.StatusCode.Should().Be(HttpStatusCode.OK);

        Guid sessionId = (await PartyAppApi.ReadJsonAsync(start)).GetProperty("sessionId").GetGuid();
        return (sessionId, admin);
    }

    private Task<HttpResponseMessage> DrawAsync(TestUser player, Guid sessionId)
    {
        _api.Authorize(player);
        return _api.Client.PostAsJsonAsync($"/api/events/{sessionId}/submit", new { payloadJson = "{}" });
    }

    [Fact]
    public async Task Pending_AsPlayer_ReturnsForbidden()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage response = await _api.Client.GetAsync("/api/events/dare/pending");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DrawThenAdminConfirms_AwardsPointsAndUpdatesPlayerData()
    {
        (Guid sessionId, TestUser admin) = await StartDareAsync();
        TestUser player = await _api.RegisterAsync();

        // Игрок тянет фант: баллы пока не начисляются
        HttpResponseMessage draw = await DrawAsync(player, sessionId);
        draw.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement drawn = await PartyAppApi.ReadJsonAsync(draw);
        drawn.GetProperty("pointsAwarded").GetInt32().Should().Be(0);
        drawn.GetProperty("data").GetProperty("status").GetString().Should().Be("pending");
        drawn.GetProperty("data").GetProperty("task").GetString().Should().NotBeNullOrWhiteSpace();

        (await _api.GetBalanceAsync(player)).Should().Be(100);

        // Второй фант нельзя
        HttpResponseMessage second = await DrawAsync(player, sessionId);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await PartyAppApi.ReadJsonAsync(second)).GetProperty("error").GetString()
            .Should().Contain("уже");

        // Админ видит очередь и подтверждает
        _api.Authorize(admin);
        JsonElement pending = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync("/api/events/dare/pending"));
        pending.GetArrayLength().Should().Be(1);
        pending[0].GetProperty("playerName").GetString().Should().Be(player.DisplayName);

        Guid assignmentId = pending[0].GetProperty("id").GetGuid();
        HttpResponseMessage confirm = await _api.Client.PostAsync(
            $"/api/events/dare/{assignmentId}/confirm", null);
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(confirm)).GetProperty("points").GetInt32().Should().Be(5);

        (await _api.GetBalanceAsync(player)).Should().Be(105);

        // Повторное подтверждение не начисляет дважды
        _api.Authorize(admin);
        HttpResponseMessage again = await _api.Client.PostAsync(
            $"/api/events/dare/{assignmentId}/confirm", null);
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _api.GetBalanceAsync(player)).Should().Be(105);

        // Игрок видит «подтверждено» после перезагрузки страницы
        _api.Authorize(player);
        JsonElement data = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync($"/api/events/{sessionId}/data"));
        data.GetProperty("player").GetProperty("dare").GetProperty("status").GetString()
            .Should().Be("confirmed");
        data.GetProperty("player").GetProperty("dare").GetProperty("points").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task Confirm_UnknownAssignment_ReturnsNotFound()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await _api.Client.PostAsync(
            $"/api/events/dare/{Guid.NewGuid()}/confirm", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}