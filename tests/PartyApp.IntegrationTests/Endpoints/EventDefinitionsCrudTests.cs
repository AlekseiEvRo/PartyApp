using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

public class EventDefinitionsCrudTests : IClassFixture<PartyAppFactory>
{
    private readonly PartyAppFactory _factory;
    private readonly PartyAppApi _api;

    public EventDefinitionsCrudTests(PartyAppFactory factory)
    {
        _factory = factory;
        _api = new PartyAppApi(factory);
    }

    private Task<HttpResponseMessage> CreateAsync(
        string type = "quiz",
        string? displayName = "Новый квиз",
        string? description = "Описание",
        string? configJson = """{"pointsPerCorrect":10,"questions":[]}""")
    {
        return _api.Client.PostAsJsonAsync(
            "/api/events/definitions",
            new { type, displayName, description, configJson });
    }

    [Fact]
    public async Task Create_AsAdmin_ReturnsCreatedDefinition()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await CreateAsync(displayName: "Квиз на корпоратив");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        JsonElement json = await PartyAppApi.ReadJsonAsync(response);
        Guid id = json.GetProperty("id").GetGuid();
        response.Headers.Location!.OriginalString.Should().Be($"/api/events/definitions/{id}");

        json.GetProperty("type").GetString().Should().Be("quiz");
        json.GetProperty("displayName").GetString().Should().Be("Квиз на корпоратив");
        json.GetProperty("description").GetString().Should().Be("Описание");
        json.GetProperty("availability").GetInt32().Should().Be((int)AvailabilityMode.Manual);
        json.GetProperty("isActive").GetBoolean().Should().BeTrue();
        json.GetProperty("createdById").GetGuid().Should().Be(admin.Id);

        EventDefinition stored = await _factory.DbAsync(db => db.EventDefinitions
            .AsNoTracking()
            .SingleAsync(d => d.Id == id));
        stored.CreatedById.Should().Be(admin.Id);
    }

    [Fact]
    public async Task Create_WithUnknownType_ReturnsBadRequest()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await CreateAsync(type: "mystery");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("error").GetString()
            .Should().Be("Неизвестный тип ивента: mystery");
    }

    [Theory]
    [InlineData("", "Название не может быть пустым")]
    [InlineData("   ", "Название не может быть пустым")]
    public async Task Create_WithInvalidDisplayName_ReturnsBadRequest(string displayName, string expectedError)
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await CreateAsync(displayName: displayName);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("error").GetString().Should().Be(expectedError);
    }

    [Fact]
    public async Task Create_WithTooLongDisplayName_ReturnsBadRequest()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await CreateAsync(displayName: new string('a', 101));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("error").GetString()
            .Should().Be("Название не должно быть длиннее 100 символов");
    }

    [Theory]
    [InlineData("not-json", "ConfigJson не является валидным JSON")]
    [InlineData("[1,2,3]", "ConfigJson должен быть JSON-объектом")]
    [InlineData("\"строка\"", "ConfigJson должен быть JSON-объектом")]
    public async Task Create_WithInvalidConfigJson_ReturnsBadRequest(string configJson, string expectedError)
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await CreateAsync(configJson: configJson);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("error").GetString().Should().Be(expectedError);
    }

    [Fact]
    public async Task Create_WithBlankFields_NormalizesThem()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await CreateAsync(
            displayName: "  Квиз  ", description: "   ", configJson: "");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        JsonElement json = await PartyAppApi.ReadJsonAsync(response);
        json.GetProperty("displayName").GetString().Should().Be("Квиз");
        json.GetProperty("description").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("configJson").GetString().Should().Be("{}");
    }

    [Fact]
    public async Task Create_WithDuration_PersistsAndStartSetsEndsAt()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/events/definitions",
            new
            {
                type = "quiz",
                displayName = "Квиз на 5 минут",
                description = (string?)null,
                configJson = "{}",
                durationMinutes = 5
            });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        JsonElement created = await PartyAppApi.ReadJsonAsync(response);
        created.GetProperty("durationMinutes").GetInt32().Should().Be(5);
        Guid id = created.GetProperty("id").GetGuid();

        HttpResponseMessage start = await _api.Client.PostAsync($"/api/events/{id}/start", null);
        start.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement started = await PartyAppApi.ReadJsonAsync(start);
        started.GetProperty("endsAt").GetDateTime().Should()
            .BeCloseTo(DateTime.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(10));

        Guid sessionId = started.GetProperty("sessionId").GetGuid();
        JsonElement available = (await PartyAppApi.ReadJsonAsync(
                await _api.Client.GetAsync("/api/events/available")))
            .EnumerateArray()
            .Single(e => e.GetProperty("sessionId").GetGuid() == sessionId);
        available.GetProperty("endsAt").GetDateTime().Should()
            .BeCloseTo(DateTime.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1441)]
    public async Task Create_WithInvalidDuration_ReturnsBadRequest(int durationMinutes)
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/events/definitions",
            new
            {
                type = "quiz",
                displayName = "Квиз",
                description = (string?)null,
                configJson = "{}",
                durationMinutes
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("error").GetString()
            .Should().Be("Длительность — от 1 до 1440 минут");
    }

    [Fact]
    public async Task Create_AsPlayer_ReturnsForbidden()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage response = await CreateAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<Guid> CreateDefinitionAsync(TestUser admin, string displayName = "Для правок")
    {
        _api.Authorize(admin);
        HttpResponseMessage response = await CreateAsync(displayName: displayName);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await PartyAppApi.ReadJsonAsync(response)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Update_ChangesEditableFieldsAndKeepsType()
    {
        TestUser admin = await _api.CreateAdminAsync();
        Guid id = await CreateDefinitionAsync(admin);

        HttpResponseMessage response = await _api.Client.PutAsJsonAsync(
            $"/api/events/definitions/{id}",
            new
            {
                displayName = "Обновлённый квиз",
                description = "Новое описание",
                configJson = """{"pointsPerCorrect":5}""",
                isActive = false
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement json = await PartyAppApi.ReadJsonAsync(response);
        json.GetProperty("type").GetString().Should().Be("quiz");
        json.GetProperty("displayName").GetString().Should().Be("Обновлённый квиз");
        json.GetProperty("description").GetString().Should().Be("Новое описание");
        json.GetProperty("configJson").GetString().Should().Be("""{"pointsPerCorrect":5}""");
        json.GetProperty("isActive").GetBoolean().Should().BeFalse();

        EventDefinition stored = await _factory.DbAsync(db => db.EventDefinitions
            .AsNoTracking()
            .SingleAsync(d => d.Id == id));
        stored.DisplayName.Should().Be("Обновлённый квиз");
        stored.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Update_WithInvalidDisplayName_ReturnsBadRequest()
    {
        TestUser admin = await _api.CreateAdminAsync();
        Guid id = await CreateDefinitionAsync(admin);

        HttpResponseMessage response = await _api.Client.PutAsJsonAsync(
            $"/api/events/definitions/{id}",
            new { displayName = "   ", description = (string?)null, configJson = "{}", isActive = true });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_WithUnknownId_ReturnsNotFound()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await _api.Client.PutAsJsonAsync(
            $"/api/events/definitions/{Guid.NewGuid()}",
            new { displayName = "Квиз", description = (string?)null, configJson = "{}", isActive = true });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_SystemDefinition_ReturnsConflict()
    {
        TestUser admin = await _api.CreateAdminAsync();
        Guid systemDefinitionId = await _factory.DbAsync(db => db.EventDefinitions
            .Where(d => d.CreatedById == null)
            .Select(d => d.Id)
            .FirstAsync());
        _api.Authorize(admin);

        HttpResponseMessage response = await _api.Client.DeleteAsync($"/api/events/definitions/{systemDefinitionId}");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("error").GetString()
            .Should().Contain("Системный ивент нельзя удалить");
    }

    [Fact]
    public async Task Delete_CustomDefinitionWithoutSessions_RemovesIt()
    {
        TestUser admin = await _api.CreateAdminAsync();
        Guid id = await CreateDefinitionAsync(admin, "Временный ивент");

        HttpResponseMessage response = await _api.Client.DeleteAsync($"/api/events/definitions/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("success").GetBoolean().Should().BeTrue();
        (await _factory.DbAsync(db => db.EventDefinitions.AnyAsync(d => d.Id == id))).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_DefinitionWithSessions_ReturnsConflict()
    {
        TestUser admin = await _api.CreateAdminAsync();
        Guid id = await CreateDefinitionAsync(admin, "Ивент с сессией");

        HttpResponseMessage start = await _api.Client.PostAsync($"/api/events/{id}/start", null);
        start.StatusCode.Should().Be(HttpStatusCode.OK);

        HttpResponseMessage response = await _api.Client.DeleteAsync($"/api/events/definitions/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("error").GetString()
            .Should().Contain("у ивента уже есть сессии");
    }

    [Fact]
    public async Task Delete_AsPlayer_ReturnsForbidden()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        Guid id = await CreateDefinitionAsync(admin);

        _api.Authorize(player);
        HttpResponseMessage response = await _api.Client.DeleteAsync($"/api/events/definitions/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}