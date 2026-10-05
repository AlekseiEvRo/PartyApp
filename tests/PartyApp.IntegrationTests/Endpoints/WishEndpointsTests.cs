using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using PartyApp.Domain.Enums;
using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

public class WishEndpointsTests : IDisposable
{
    private readonly PartyAppFactory _factory = new();
    private readonly PartyAppApi _api;

    public WishEndpointsTests()
    {
        _api = new PartyAppApi(_factory);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private async Task<JsonElement> SubmitAsync(TestUser user, string text)
    {
        _api.Authorize(user);
        HttpResponseMessage response = await _api.Client.PostAsJsonAsync("/api/wishes", new { text });

        response.StatusCode.Should().Be(
            HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());

        return await PartyAppApi.ReadJsonAsync(response);
    }

    private async Task<JsonElement> GetPageAsync(TestUser user)
    {
        _api.Authorize(user);
        HttpResponseMessage response = await _api.Client.GetAsync("/api/wishes");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await PartyAppApi.ReadJsonAsync(response);
    }

    [Fact]
    public async Task Submit_WithoutToken_ReturnsUnauthorized()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/wishes", new { text = "Ура!" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Submit_GoesToModeration_AndBecomesVisibleAfterApproval()
    {
        TestUser author = await _api.RegisterAsync();
        TestUser other = await _api.RegisterAsync();
        TestUser admin = await _api.CreateAdminAsync();

        JsonElement created = await SubmitAsync(author, "С днём рождения!");
        Guid wishId = created.GetProperty("id").GetGuid();
        created.GetProperty("status").GetString().Should().Be("Pending");

        // Другой игрок ничего не видит, автор видит своё на модерации
        (await GetPageAsync(other)).GetProperty("total").GetInt32().Should().Be(0);

        JsonElement ownPage = await GetPageAsync(author);
        ownPage.GetProperty("total").GetInt32().Should().Be(1);
        ownPage.GetProperty("items")[0].GetProperty("status").GetString().Should().Be("Pending");
        ownPage.GetProperty("items")[0].GetProperty("isMine").GetBoolean().Should().BeTrue();

        // После одобрения пожелание видят все
        _api.Authorize(admin);
        HttpResponseMessage approve = await _api.Client.PostAsync($"/api/wishes/{wishId}/approve", null);
        approve.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement approvedPage = await GetPageAsync(other);
        approvedPage.GetProperty("total").GetInt32().Should().Be(1);
        JsonElement item = approvedPage.GetProperty("items")[0];
        item.GetProperty("text").GetString().Should().Be("С днём рождения!");
        item.GetProperty("playerName").GetString().Should().Be(author.DisplayName);
        item.GetProperty("status").GetString().Should().Be("Approved");
    }

    [Fact]
    public async Task Submit_RejectedWish_StaysHiddenFromOthers()
    {
        TestUser author = await _api.RegisterAsync();
        TestUser other = await _api.RegisterAsync();
        TestUser admin = await _api.CreateAdminAsync();

        Guid wishId = (await SubmitAsync(author, "Спорный текст")).GetProperty("id").GetGuid();

        _api.Authorize(admin);
        HttpResponseMessage reject = await _api.Client.PostAsync($"/api/wishes/{wishId}/reject", null);
        reject.StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetPageAsync(other)).GetProperty("total").GetInt32().Should().Be(0);

        // Автор видит отклонённое
        JsonElement ownPage = await GetPageAsync(author);
        ownPage.GetProperty("items")[0].GetProperty("status").GetString().Should().Be("Rejected");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Submit_WithEmptyText_ReturnsBadRequest(string text)
    {
        TestUser user = await _api.RegisterAsync();
        _api.Authorize(user);

        HttpResponseMessage response = await _api.Client.PostAsJsonAsync("/api/wishes", new { text });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Submit_TooLongText_ReturnsBadRequest()
    {
        TestUser user = await _api.RegisterAsync();
        _api.Authorize(user);

        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/wishes", new { text = new string('а', 501) });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Submit_WhenModerationDisabled_IsVisibleImmediately()
    {
        using PartyAppFactory factory = new()
        {
            ConfigureOverrides = settings => settings["Wishes:RequireModeration"] = "false"
        };
        PartyAppApi api = new(factory);
        TestUser author = await api.RegisterAsync();
        TestUser other = await api.RegisterAsync();

        api.Authorize(author);
        HttpResponseMessage response = await api.Client.PostAsJsonAsync("/api/wishes", new { text = "Сразу видно!" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("status").GetString().Should().Be("Approved");

        api.Authorize(other);
        JsonElement page = await PartyAppApi.ReadJsonAsync(await api.Client.GetAsync("/api/wishes"));
        page.GetProperty("total").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Delete_ForeignWish_ReturnsForbidden_OwnRemovesIt()
    {
        TestUser author = await _api.RegisterAsync();
        TestUser admin = await _api.CreateAdminAsync();
        Guid wishId = (await SubmitAsync(author, "Моё пожелание")).GetProperty("id").GetGuid();

        TestUser stranger = await _api.RegisterAsync();
        _api.Authorize(stranger);
        HttpResponseMessage forbidden = await _api.Client.DeleteAsync($"/api/wishes/{wishId}");
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Админ может удалить чужое
        _api.Authorize(admin);
        HttpResponseMessage deleted = await _api.Client.DeleteAsync($"/api/wishes/{wishId}");
        deleted.StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetPageAsync(admin)).GetProperty("total").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task SuperAdmin_SeesPendingWishesAndCanDeleteForeignOnes()
    {
        TestUser author = await _api.RegisterAsync();
        TestUser superAdmin = await _api.CreateUserAsync(
            PartyAppApi.UniqueUsername("super"), UserRole.SuperAdmin);
        Guid wishId = (await SubmitAsync(author, "Проверка супер-админа")).GetProperty("id").GetGuid();

        // Чужое пожелание на модерации видно в очереди супер-админа
        JsonElement page = await GetPageAsync(superAdmin);
        page.GetProperty("total").GetInt32().Should().Be(1);
        page.GetProperty("items")[0].GetProperty("status").GetString().Should().Be("Pending");

        // И он может удалить чужое пожелание
        _api.Authorize(superAdmin);
        (await _api.Client.DeleteAsync($"/api/wishes/{wishId}")).StatusCode
            .Should().Be(HttpStatusCode.OK);

        (await GetPageAsync(superAdmin)).GetProperty("total").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Moderation_AsPlayer_ReturnsForbidden()
    {
        TestUser author = await _api.RegisterAsync();
        Guid wishId = (await SubmitAsync(author, "Проверка прав")).GetProperty("id").GetGuid();

        _api.Authorize(author);
        HttpResponseMessage response = await _api.Client.PostAsync($"/api/wishes/{wishId}/approve", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}