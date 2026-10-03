using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

public class EventPackEndpointsTests : IClassFixture<PartyAppFactory>
{
    private readonly PartyAppApi _api;

    public EventPackEndpointsTests(PartyAppFactory factory)
    {
        _api = new PartyAppApi(factory);
    }

    [Fact]
    public async Task Export_AsAdmin_ReturnsJsonWithDefinitions()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await _api.Client.GetAsync("/api/admin/events/definitions/export");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        response.Content.Headers.ContentDisposition!.FileName.Should().Contain("party-events-");

        JsonElement pack = await PartyAppApi.ReadJsonAsync(response);
        pack.GetProperty("version").GetInt32().Should().Be(1);

        List<JsonElement> definitions = pack.GetProperty("definitions").EnumerateArray().ToList();
        definitions.Should().NotBeEmpty();
        definitions.Should().Contain(d => d.GetProperty("type").GetString() == "quick_checkin");
    }

    [Fact]
    public async Task Export_AsPlayer_ReturnsForbidden()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        (await _api.Client.GetAsync("/api/admin/events/definitions/export")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Import_AddsNewDefinitionsAndSkipsDuplicates()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        var pack = new
        {
            version = 1,
            definitions = new object[]
            {
                new
                {
                    type = "quiz",
                    displayName = "Импортированный квиз",
                    description = "Из пакета",
                    configJson = """{"timeLimitSec":30,"pointsPerCorrect":5,"questions":[]}""",
                    durationMinutes = 20
                },
                new
                {
                    type = "quiz",
                    displayName = "Тост за именинника", // уже есть среди системных
                    configJson = "{}",
                    durationMinutes = (int?)null
                },
                new
                {
                    type = "unknown_type",
                    displayName = "Сломанный",
                    configJson = "{}",
                    durationMinutes = (int?)null
                }
            }
        };

        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/admin/events/definitions/import", pack);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement result = await PartyAppApi.ReadJsonAsync(response);
        result.GetProperty("added").GetInt32().Should().Be(1);
        result.GetProperty("skipped").GetInt32().Should().Be(1);
        result.GetProperty("errors").GetArrayLength().Should().Be(1);
        result.GetProperty("errors")[0].GetProperty("error").GetString()
            .Should().Be("Неизвестный тип ивента: unknown_type");

        JsonElement definitions = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync("/api/events/definitions?includeInactive=true"));
        JsonElement imported = definitions.EnumerateArray()
            .Single(d => d.GetProperty("displayName").GetString() == "Импортированный квиз");
        imported.GetProperty("durationMinutes").GetInt32().Should().Be(20);
        imported.GetProperty("createdById").GetGuid().Should().Be(admin.Id);
    }

    [Fact]
    public async Task Import_WithBrokenConfigJson_ReportsError()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        var pack = new
        {
            definitions = new object[]
            {
                new
                {
                    type = "quiz",
                    displayName = "Кривой конфиг",
                    configJson = "not-json",
                    durationMinutes = (int?)null
                }
            }
        };

        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/admin/events/definitions/import", pack);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement result = await PartyAppApi.ReadJsonAsync(response);
        result.GetProperty("added").GetInt32().Should().Be(0);
        result.GetProperty("errors")[0].GetProperty("error").GetString()
            .Should().Be("ConfigJson не является валидным JSON");
    }

    [Fact]
    public async Task Import_WithoutDefinitions_ReturnsBadRequest()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/admin/events/definitions/import", new { definitions = Array.Empty<object>() });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Import_AsPlayer_ReturnsForbidden()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/admin/events/definitions/import",
            new { definitions = new[] { new { type = "quiz", displayName = "Чужой" } } });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
