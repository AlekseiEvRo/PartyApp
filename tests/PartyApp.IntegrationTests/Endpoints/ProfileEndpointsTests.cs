using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

public class ProfileEndpointsTests : IClassFixture<PartyAppFactory>
{
    private static readonly byte[] JpegBytes = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 };

    private readonly PartyAppFactory _factory;
    private readonly PartyAppApi _api;

    public ProfileEndpointsTests(PartyAppFactory factory)
    {
        _factory = factory;
        _api = new PartyAppApi(factory);
    }

    private static MultipartFormDataContent AvatarForm(byte[] bytes, string fileName = "avatar.jpg")
    {
        ByteArrayContent file = new(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

        MultipartFormDataContent form = new();
        form.Add(file, "file", fileName);
        return form;
    }

    [Fact]
    public async Task Profile_WithoutToken_ReturnsUnauthorized()
    {
        HttpResponseMessage response = await _api.Client.GetAsync("/api/profile");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_ReturnsProfile()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage response = await _api.Client.GetAsync("/api/profile");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement profile = await PartyAppApi.ReadJsonAsync(response);
        profile.GetProperty("userId").GetGuid().Should().Be(player.Id);
        profile.GetProperty("displayName").GetString().Should().Be(player.DisplayName);
        profile.GetProperty("hasAvatar").GetBoolean().Should().BeFalse();
        profile.GetProperty("statusEmoji").ValueKind.Should().Be(JsonValueKind.Null);
        profile.GetProperty("maxAvatarBytes").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Patch_UpdatesAndClearsStatus()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage response = await _api.Client.PatchAsJsonAsync(
            "/api/profile", new { statusEmoji = "🎂" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("statusEmoji").GetString()
            .Should().Be("🎂");

        HttpResponseMessage cleared = await _api.Client.PatchAsJsonAsync(
            "/api/profile", new { statusEmoji = "   " });

        cleared.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(cleared)).GetProperty("statusEmoji").ValueKind
            .Should().Be(JsonValueKind.Null);

        JsonElement profile = await PartyAppApi.ReadJsonAsync(await _api.Client.GetAsync("/api/profile"));
        profile.GetProperty("statusEmoji").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Patch_TooLongStatus_ReturnsBadRequest()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage response = await _api.Client.PatchAsJsonAsync(
            "/api/profile", new { statusEmoji = new string('x', 17) });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UploadAvatar_StoresAndServesIt()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage upload = await _api.Client.PostAsync("/api/profile/avatar", AvatarForm(JpegBytes));

        upload.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(upload)).GetProperty("hasAvatar").GetBoolean().Should().BeTrue();

        HttpResponseMessage content = await _api.Client.GetAsync($"/api/users/{player.Id}/avatar");
        content.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
        (await content.Content.ReadAsByteArrayAsync()).Should().Equal(JpegBytes);

        JsonElement profile = await PartyAppApi.ReadJsonAsync(await _api.Client.GetAsync("/api/profile"));
        profile.GetProperty("hasAvatar").GetBoolean().Should().BeTrue();
        profile.GetProperty("profileUpdatedAt").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task UploadAvatar_NonImage_ReturnsBadRequest()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage response = await _api.Client.PostAsync(
            "/api/profile/avatar",
            AvatarForm("это не картинка"u8.ToArray(), "avatar.jpg"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("error").GetString()
            .Should().Be("Поддерживаются только JPEG, PNG и WebP");
    }

    [Fact]
    public async Task UploadAvatar_TooLarge_ReturnsBadRequest()
    {
        using PartyAppFactory factory = new()
        {
            ConfigureOverrides = settings => settings["Profile:MaxAvatarBytes"] = "5"
        };
        PartyAppApi api = new(factory);
        TestUser player = await api.RegisterAsync();
        api.Authorize(player);

        HttpResponseMessage response = await api.Client.PostAsync("/api/profile/avatar", AvatarForm(JpegBytes));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UploadAvatar_ReplacesOldFile()
    {
        using PartyAppFactory factory = new();
        PartyAppApi api = new(factory);
        TestUser player = await api.RegisterAsync();
        api.Authorize(player);

        (await api.Client.PostAsync("/api/profile/avatar", AvatarForm(JpegBytes))).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await api.Client.PostAsync("/api/profile/avatar", AvatarForm(JpegBytes))).StatusCode
            .Should().Be(HttpStatusCode.OK);

        string avatarDirectory = Path.Combine(
            Path.GetDirectoryName(factory.DatabasePath)!,
            "uploads",
            "avatars");

        Directory.GetFiles(avatarDirectory, "*", SearchOption.AllDirectories).Should().HaveCount(1);
    }

    [Fact]
    public async Task DeleteAvatar_RemovesItAndIsIdempotent()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        (await _api.Client.PostAsync("/api/profile/avatar", AvatarForm(JpegBytes))).StatusCode
            .Should().Be(HttpStatusCode.OK);

        HttpResponseMessage delete = await _api.Client.DeleteAsync("/api/profile/avatar");
        delete.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(delete)).GetProperty("hasAvatar").GetBoolean().Should().BeFalse();

        (await _api.Client.GetAsync($"/api/users/{player.Id}/avatar")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        // Повторное удаление не ошибка
        (await _api.Client.DeleteAsync("/api/profile/avatar")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Avatar_WithoutAvatar_ReturnsNotFound()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage response = await _api.Client.GetAsync($"/api/users/{player.Id}/avatar");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Leaderboard_IncludesProfileVersion()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);
        await _api.Client.PatchAsJsonAsync("/api/profile", new { statusEmoji = "🔥" });

        _api.Authorize(admin);
        HttpResponseMessage response = await _api.Client.GetAsync("/api/admin/leaderboard");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement row = (await PartyAppApi.ReadJsonAsync(response)).EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == player.Id);
        row.GetProperty("profileUpdatedAt").ValueKind.Should().NotBe(JsonValueKind.Null);
    }
}
