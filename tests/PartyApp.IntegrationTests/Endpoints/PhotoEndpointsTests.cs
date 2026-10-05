using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using PartyApp.Domain.Enums;
using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

public class PhotoEndpointsTests : IDisposable
{
    // Сигнатура JPEG — сервер проверяет тип по содержимому, а не по заголовку клиента
    private static readonly byte[] JpegBytes = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 };

    private readonly PartyAppFactory _factory = new()
    {
        // В большинстве тестов удобнее сразу одобренные фото; премодерация проверяется отдельно
        ConfigureOverrides = settings => settings["Photos:RequireModeration"] = "false"
    };
    private readonly PartyAppApi _api;

    public PhotoEndpointsTests()
    {
        _api = new PartyAppApi(_factory);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private static MultipartFormDataContent CreateUpload(
        byte[] bytes,
        string fileName = "photo.jpg",
        string contentType = "image/jpeg",
        string? caption = null)
    {
        ByteArrayContent fileContent = new(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        MultipartFormDataContent form = new();
        form.Add(fileContent, "file", fileName);

        if (caption is not null)
            form.Add(new StringContent(caption), "caption");

        return form;
    }

    private async Task<JsonElement> UploadAsync(
        TestUser user,
        byte[]? bytes = null,
        string contentType = "image/jpeg",
        string? caption = null)
    {
        _api.Authorize(user);
        HttpResponseMessage response = await _api.Client.PostAsync(
            "/api/photos", CreateUpload(bytes ?? JpegBytes, contentType: contentType, caption: caption));

        response.StatusCode.Should().Be(
            HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());

        return await PartyAppApi.ReadJsonAsync(response);
    }

    private async Task<JsonElement> GetPageAsync(TestUser user, string query = "")
    {
        _api.Authorize(user);
        HttpResponseMessage response = await _api.Client.GetAsync($"/api/photos{query}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await PartyAppApi.ReadJsonAsync(response);
    }

    [Fact]
    public async Task Upload_WithoutToken_ReturnsUnauthorized()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync("/api/photos", CreateUpload(JpegBytes));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Upload_Jpeg_AppearsInFeedWithMetadataAndContent()
    {
        TestUser user = await _api.RegisterAsync();

        JsonElement created = await UploadAsync(user);
        Guid photoId = created.GetProperty("id").GetGuid();
        created.GetProperty("status").GetString().Should().Be("Approved");

        JsonElement page = await GetPageAsync(user);
        page.GetProperty("total").GetInt32().Should().Be(1);

        JsonElement item = page.GetProperty("items")[0];
        item.GetProperty("id").GetGuid().Should().Be(photoId);
        item.GetProperty("uploadedByName").GetString().Should().Be(user.DisplayName);
        item.GetProperty("status").GetString().Should().Be("Approved");
        item.GetProperty("likesCount").GetInt32().Should().Be(0);
        item.GetProperty("isMine").GetBoolean().Should().BeTrue();

        HttpResponseMessage content = await _api.Client.GetAsync($"/api/photos/{photoId}/content");
        content.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Content.Headers.ContentType?.MediaType.Should().Be("image/jpeg");
        (await content.Content.ReadAsByteArrayAsync()).Should().Equal(JpegBytes);
    }

    [Fact]
    public async Task Upload_WithCaption_ReturnsTrimmedCaptionInFeed()
    {
        TestUser user = await _api.RegisterAsync();

        await UploadAsync(user, caption: "  Лучший момент вечера  ");

        JsonElement page = await GetPageAsync(user);
        page.GetProperty("items")[0].GetProperty("caption").GetString()
            .Should().Be("Лучший момент вечера");
    }

    [Fact]
    public async Task Upload_WhitespaceCaption_IsStoredAsNull()
    {
        TestUser user = await _api.RegisterAsync();

        await UploadAsync(user, caption: "   ");

        JsonElement page = await GetPageAsync(user);
        page.GetProperty("items")[0].GetProperty("caption").ValueKind
            .Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Upload_TooLongCaption_ReturnsBadRequest()
    {
        TestUser user = await _api.RegisterAsync();
        _api.Authorize(user);

        HttpResponseMessage response = await _api.Client.PostAsync(
            "/api/photos", CreateUpload(JpegBytes, caption: new string('а', 201)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("error").GetString()
            .Should().Contain("200");
    }

    [Fact]
    public async Task Upload_NonImage_ReturnsBadRequest()
    {
        TestUser user = await _api.RegisterAsync();
        _api.Authorize(user);

        HttpResponseMessage response = await _api.Client.PostAsync(
            "/api/photos",
            CreateUpload("это не картинка"u8.ToArray(), contentType: "image/jpeg"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("error").GetString()
            .Should().Contain("JPEG");
    }

    [Fact]
    public async Task Like_TogglesAndCounts()
    {
        TestUser author = await _api.RegisterAsync();
        TestUser other = await _api.RegisterAsync();
        Guid photoId = (await UploadAsync(author)).GetProperty("id").GetGuid();

        _api.Authorize(other);
        HttpResponseMessage like = await _api.Client.PostAsync($"/api/photos/{photoId}/like", null);
        like.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement liked = await PartyAppApi.ReadJsonAsync(like);
        liked.GetProperty("liked").GetBoolean().Should().BeTrue();
        liked.GetProperty("likesCount").GetInt32().Should().Be(1);

        JsonElement otherPage = await GetPageAsync(other);
        otherPage.GetProperty("items")[0].GetProperty("likedByMe").GetBoolean().Should().BeTrue();

        HttpResponseMessage unlike = await _api.Client.PostAsync($"/api/photos/{photoId}/like", null);
        JsonElement unliked = await PartyAppApi.ReadJsonAsync(unlike);
        unliked.GetProperty("liked").GetBoolean().Should().BeFalse();
        unliked.GetProperty("likesCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Delete_ForeignPhoto_ReturnsForbidden_OwnPhotoRemovesIt()
    {
        TestUser author = await _api.RegisterAsync();
        TestUser stranger = await _api.RegisterAsync();
        Guid photoId = (await UploadAsync(author)).GetProperty("id").GetGuid();

        _api.Authorize(stranger);
        HttpResponseMessage forbidden = await _api.Client.DeleteAsync($"/api/photos/{photoId}");
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        _api.Authorize(author);
        HttpResponseMessage deleted = await _api.Client.DeleteAsync($"/api/photos/{photoId}");
        deleted.StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetPageAsync(author)).GetProperty("total").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Upload_WhenModerationRequired_IsHiddenUntilApproved()
    {
        using PartyAppFactory factory = new()
        {
            ConfigureOverrides = settings => settings["Photos:RequireModeration"] = "true"
        };
        PartyAppApi api = new(factory);
        TestUser author = await api.RegisterAsync();
        TestUser other = await api.RegisterAsync();
        TestUser admin = await api.CreateAdminAsync();

        api.Authorize(author);
        HttpResponseMessage upload = await api.Client.PostAsync("/api/photos", CreateUpload(JpegBytes));
        upload.StatusCode.Should().Be(HttpStatusCode.Created);
        Guid photoId = (await PartyAppApi.ReadJsonAsync(upload)).GetProperty("id").GetGuid();

        // Другой игрок фото не видит
        api.Authorize(other);
        JsonElement otherPage = await PartyAppApi.ReadJsonAsync(
            await api.Client.GetAsync("/api/photos"));
        otherPage.GetProperty("total").GetInt32().Should().Be(0);
        (await api.Client.GetAsync($"/api/photos/{photoId}/content")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        // Автор видит своё со статусом Pending
        api.Authorize(author);
        JsonElement ownPage = await PartyAppApi.ReadJsonAsync(
            await api.Client.GetAsync("/api/photos"));
        ownPage.GetProperty("total").GetInt32().Should().Be(1);
        ownPage.GetProperty("items")[0].GetProperty("status").GetString().Should().Be("Pending");

        // После одобрения фото видят все
        api.Authorize(admin);
        HttpResponseMessage approve = await api.Client.PostAsync($"/api/photos/{photoId}/approve", null);
        approve.StatusCode.Should().Be(HttpStatusCode.OK);

        api.Authorize(other);
        JsonElement approvedPage = await PartyAppApi.ReadJsonAsync(
            await api.Client.GetAsync("/api/photos"));
        approvedPage.GetProperty("total").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task SuperAdmin_SeesPendingPhotosAndCanModerateForeignOnes()
    {
        using PartyAppFactory factory = new()
        {
            ConfigureOverrides = settings => settings["Photos:RequireModeration"] = "true"
        };
        PartyAppApi api = new(factory);
        TestUser author = await api.RegisterAsync();
        TestUser superAdmin = await api.CreateUserAsync(
            PartyAppApi.UniqueUsername("super"), UserRole.SuperAdmin);

        api.Authorize(author);
        HttpResponseMessage upload = await api.Client.PostAsync("/api/photos", CreateUpload(JpegBytes));
        Guid photoId = (await PartyAppApi.ReadJsonAsync(upload)).GetProperty("id").GetGuid();

        // Супер-админ видит чужое фото в очереди модерации и может открыть его контент
        api.Authorize(superAdmin);
        JsonElement page = await PartyAppApi.ReadJsonAsync(await api.Client.GetAsync("/api/photos"));
        page.GetProperty("total").GetInt32().Should().Be(1);
        page.GetProperty("items")[0].GetProperty("status").GetString().Should().Be("Pending");
        (await api.Client.GetAsync($"/api/photos/{photoId}/content")).StatusCode
            .Should().Be(HttpStatusCode.OK);

        // Модерация и удаление чужого фото тоже доступны
        (await api.Client.PostAsync($"/api/photos/{photoId}/approve", null)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await api.Client.DeleteAsync($"/api/photos/{photoId}")).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Upload_WithoutModerationOverride_GoesToPending()
    {
        // Без переопределений действует конфиг приложения: фото ждёт модерации
        using PartyAppFactory factory = new();
        PartyAppApi api = new(factory);
        TestUser user = await api.RegisterAsync();

        api.Authorize(user);
        HttpResponseMessage response = await api.Client.PostAsync("/api/photos", CreateUpload(JpegBytes));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("status").GetString()
            .Should().Be("Pending");
    }

    [Fact]
    public async Task Approve_AwardsPhotoPointsOnce()
    {
        using PartyAppFactory factory = new()
        {
            ConfigureOverrides = settings => settings["Photos:RequireModeration"] = "true"
        };
        PartyAppApi api = new(factory);
        TestUser author = await api.RegisterAsync();
        TestUser admin = await api.CreateAdminAsync();

        api.Authorize(author);
        HttpResponseMessage upload = await api.Client.PostAsync("/api/photos", CreateUpload(JpegBytes));
        Guid photoId = (await PartyAppApi.ReadJsonAsync(upload)).GetProperty("id").GetGuid();
        (await api.GetBalanceAsync(author)).Should().Be(100);

        // Одобрение начисляет баллы автору
        api.Authorize(admin);
        (await api.Client.PostAsync($"/api/photos/{photoId}/approve", null)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await api.GetBalanceAsync(author)).Should().Be(105);

        // Повторное одобрение после отклонения не удваивает награду
        api.Authorize(admin);
        (await api.Client.PostAsync($"/api/photos/{photoId}/reject", null)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await api.Client.PostAsync($"/api/photos/{photoId}/approve", null)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await api.GetBalanceAsync(author)).Should().Be(105);
    }

    [Fact]
    public async Task Upload_WhenAutoApproved_AwardsPhotoPoints()
    {
        using PartyAppFactory factory = new()
        {
            ConfigureOverrides = settings => settings["Photos:RequireModeration"] = "false"
        };
        PartyAppApi api = new(factory);
        TestUser author = await api.RegisterAsync();

        api.Authorize(author);
        HttpResponseMessage upload = await api.Client.PostAsync("/api/photos", CreateUpload(JpegBytes));
        upload.StatusCode.Should().Be(HttpStatusCode.Created);

        (await api.GetBalanceAsync(author)).Should().Be(105);
    }

    [Fact]
    public async Task RewardSettings_AreEditableByAdminOnlyAndApplyToNewPhotos()
    {
        using PartyAppFactory factory = new()
        {
            ConfigureOverrides = settings => settings["Photos:RequireModeration"] = "false"
        };
        PartyAppApi api = new(factory);
        TestUser admin = await api.CreateAdminAsync();
        TestUser player = await api.RegisterAsync();

        api.Authorize(admin);
        HttpResponseMessage defaults = await api.Client.GetAsync("/api/photos/settings");
        defaults.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(defaults)).GetProperty("photoApprovedPoints").GetInt32()
            .Should().Be(5);

        // Игрок менять не может
        api.Authorize(player);
        (await api.Client.PutAsJsonAsync("/api/photos/settings", new { photoApprovedPoints = 20 }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Админ меняет — новое значение применяется к следующему фото
        api.Authorize(admin);
        HttpResponseMessage update = await api.Client.PutAsJsonAsync(
            "/api/photos/settings", new { photoApprovedPoints = 20 });
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(update)).GetProperty("photoApprovedPoints").GetInt32()
            .Should().Be(20);

        TestUser author = await api.RegisterAsync();
        api.Authorize(author);
        await api.Client.PostAsync("/api/photos", CreateUpload(JpegBytes));

        (await api.GetBalanceAsync(author)).Should().Be(120);

        // Вне диапазона — ошибка
        api.Authorize(admin);
        (await api.Client.PutAsJsonAsync("/api/photos/settings", new { photoApprovedPoints = -1 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Upload_TooLarge_ReturnsBadRequest()
    {
        using PartyAppFactory factory = new()
        {
            ConfigureOverrides = settings => settings["Photos:MaxSizeBytes"] = "4"
        };
        PartyAppApi api = new(factory);
        TestUser user = await api.RegisterAsync();

        api.Authorize(user);
        HttpResponseMessage response = await api.Client.PostAsync("/api/photos", CreateUpload(JpegBytes));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}