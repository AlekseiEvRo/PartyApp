using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using PartyApp.Domain.Enums;
using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

public class ShopEndpointsTests : IDisposable
{
    private readonly PartyAppFactory _factory = new();
    private readonly PartyAppApi _api;

    public ShopEndpointsTests()
    {
        _api = new PartyAppApi(_factory);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private async Task<Guid> CreateItemAsync(
        TestUser admin,
        string name = "Коктейль",
        int price = 30,
        int? stock = null,
        bool isActive = true)
    {
        _api.Authorize(admin);
        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/shop/items",
            new { name, description = "Приз за баллы", price, stock, isActive });

        response.StatusCode.Should().Be(
            HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());

        return (await PartyAppApi.ReadJsonAsync(response)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Items_AsPlayer_ReturnsOnlyActiveSortedByPrice()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();

        await CreateItemAsync(admin, name: "Дорогой", price: 90);
        await CreateItemAsync(admin, name: "Дешёвый", price: 10);
        await CreateItemAsync(admin, name: "Выключенный", price: 50, isActive: false);

        _api.Authorize(player);
        JsonElement items = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync("/api/shop/items"));

        items.GetArrayLength().Should().Be(2);
        items[0].GetProperty("name").GetString().Should().Be("Дешёвый");
        items[1].GetProperty("name").GetString().Should().Be("Дорогой");
    }

    [Fact]
    public async Task Items_AsSuperAdminWithIncludeInactive_ReturnsInactiveOnes()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser superAdmin = await _api.CreateUserAsync(
            PartyAppApi.UniqueUsername("super"), UserRole.SuperAdmin);

        await CreateItemAsync(admin, name: "Активный", price: 10);
        await CreateItemAsync(admin, name: "Выключенный", price: 50, isActive: false);

        _api.Authorize(superAdmin);
        JsonElement items = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync("/api/shop/items?includeInactive=true"));

        items.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Buy_WithEnoughPoints_DecreasesBalanceAndStock()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        Guid itemId = await CreateItemAsync(admin, price: 30, stock: 2);

        _api.Authorize(player);
        HttpResponseMessage response = await _api.Client.PostAsync($"/api/shop/items/{itemId}/buy", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement json = await PartyAppApi.ReadJsonAsync(response);
        json.GetProperty("newBalance").GetInt32().Should().Be(70);

        JsonElement items = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync("/api/shop/items?includeInactive=true"));
        items[0].GetProperty("stock").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Buy_NotEnoughPoints_ReturnsConflictAndKeepsBalance()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        Guid itemId = await CreateItemAsync(admin, price: 500);

        _api.Authorize(player);
        HttpResponseMessage response = await _api.Client.PostAsync($"/api/shop/items/{itemId}/buy", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("error").GetString()
            .Should().Be("Недостаточно баллов");
        (await _api.GetBalanceAsync(player)).Should().Be(100);
    }

    [Fact]
    public async Task Buy_OutOfStock_ReturnsConflict()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser first = await _api.RegisterAsync();
        TestUser second = await _api.RegisterAsync();
        Guid itemId = await CreateItemAsync(admin, price: 10, stock: 1);

        _api.Authorize(first);
        (await _api.Client.PostAsync($"/api/shop/items/{itemId}/buy", null)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        _api.Authorize(second);
        HttpResponseMessage response = await _api.Client.PostAsync($"/api/shop/items/{itemId}/buy", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("error").GetString()
            .Should().Be("Товар закончился");
        (await _api.GetBalanceAsync(second)).Should().Be(100);
    }

    [Fact]
    public async Task Purchases_PlayerSeesOwn_AdminFulfills()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        Guid itemId = await CreateItemAsync(admin, name: "Шампанское", price: 40);

        _api.Authorize(player);
        Guid purchaseId = (await PartyAppApi.ReadJsonAsync(
                await _api.Client.PostAsync($"/api/shop/items/{itemId}/buy", null)))
            .GetProperty("purchaseId").GetGuid();

        JsonElement own = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync("/api/shop/purchases/my"));
        own.GetArrayLength().Should().Be(1);
        own[0].GetProperty("itemName").GetString().Should().Be("Шампанское");
        own[0].GetProperty("isFulfilled").GetBoolean().Should().BeFalse();

        _api.Authorize(admin);
        HttpResponseMessage fulfill = await _api.Client.PostAsync(
            $"/api/shop/purchases/{purchaseId}/fulfill", null);
        fulfill.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement all = await PartyAppApi.ReadJsonAsync(
            await _api.Client.GetAsync("/api/shop/purchases"));
        all[0].GetProperty("isFulfilled").GetBoolean().Should().BeTrue();
        all[0].GetProperty("playerName").GetString().Should().Be(player.DisplayName);
    }

    [Fact]
    public async Task Delete_ItemWithPurchases_ReturnsConflict()
    {
        TestUser admin = await _api.CreateAdminAsync();
        TestUser player = await _api.RegisterAsync();
        Guid itemId = await CreateItemAsync(admin, price: 10);

        _api.Authorize(player);
        (await _api.Client.PostAsync($"/api/shop/items/{itemId}/buy", null)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        _api.Authorize(admin);
        HttpResponseMessage response = await _api.Client.DeleteAsync($"/api/shop/items/{itemId}");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("   ", 10)]
    [InlineData("Приз", 0)]
    [InlineData("Приз", -5)]
    public async Task CreateItem_InvalidData_ReturnsBadRequest(string name, int price)
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await _api.Client.PostAsJsonAsync(
            "/api/shop/items",
            new { name, description = (string?)null, price, stock = (int?)null, isActive = true });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Manage_AsPlayer_ReturnsForbidden()
    {
        TestUser player = await _api.RegisterAsync();
        _api.Authorize(player);

        HttpResponseMessage list = await _api.Client.GetAsync("/api/shop/purchases");
        HttpResponseMessage create = await _api.Client.PostAsJsonAsync(
            "/api/shop/items", new { name = "Приз", price = 10 });

        list.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}