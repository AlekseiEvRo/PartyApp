using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using PartyApp.Api.Modules.Auth.Requests;
using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

public class AuthEndpointsTests : IClassFixture<PartyAppFactory>
{
    private readonly PartyAppFactory _factory;
    private readonly PartyAppApi _api;

    public AuthEndpointsTests(PartyAppFactory factory)
    {
        _factory = factory;
        _api = new PartyAppApi(factory);
    }

    [Fact]
    public async Task Register_CreatesPlayerAndReturnsToken()
    {
        string username = PartyAppApi.UniqueUsername("alice");

        HttpResponseMessage response = await _api.RegisterRawAsync(username, displayName: "Алиса");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement json = await PartyAppApi.ReadJsonAsync(response);
        json.GetProperty("accessToken").GetString().Should().NotBeNullOrWhiteSpace();
        json.GetProperty("username").GetString().Should().Be(username);
        json.GetProperty("displayName").GetString().Should().Be("Алиса");
        json.GetProperty("role").GetString().Should().Be("Player");
        json.GetProperty("userId").GetGuid().Should().NotBe(Guid.Empty);
    }

    [Theory]
    [InlineData("", "secret123", "Имя пользователя не может быть пустым")]
    [InlineData("   ", "secret123", "Имя пользователя не может быть пустым")]
    [InlineData("bob_invalid_password", "123", "Пароль должен содержать минимум 6 символов")]
    public async Task Register_WithInvalidData_ReturnsBadRequest(string username, string password, string expectedError)
    {
        HttpResponseMessage response = await _api.RegisterRawAsync(username, password, "Алиса");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonElement json = await PartyAppApi.ReadJsonAsync(response);
        json.GetProperty("error").GetString().Should().Be(expectedError);
    }

    [Fact]
    public async Task Register_WithDuplicateUsername_ReturnsBadRequest()
    {
        string username = PartyAppApi.UniqueUsername("dup");
        await _api.RegisterRawAsync(username);

        HttpResponseMessage response = await _api.RegisterRawAsync(username.ToUpperInvariant());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonElement json = await PartyAppApi.ReadJsonAsync(response);
        json.GetProperty("error").GetString().Should().Be("Пользователь с таким именем уже существует");
    }

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsToken()
    {
        TestUser registered = await _api.RegisterAsync();

        HttpResponseMessage response = await _api.LoginRawAsync(registered.Username.ToUpperInvariant());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement json = await PartyAppApi.ReadJsonAsync(response);
        json.GetProperty("userId").GetGuid().Should().Be(registered.Id);
    }

    [Fact]
    public async Task Login_WithUnknownUser_ReturnsBadRequestWithGenericMessage()
    {
        HttpResponseMessage response = await _api.LoginRawAsync("unknown-user-" + Guid.NewGuid().ToString("N"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("error").GetString()
            .Should().Be("Неверный логин или пароль");
    }

    [Fact]
    public async Task Login_WithWrongPassword_ReturnsSameGenericMessage()
    {
        TestUser user = await _api.RegisterAsync();

        HttpResponseMessage response = await _api.LoginRawAsync(user.Username, "wrong-password");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("error").GetString()
            .Should().Be("Неверный логин или пароль");
    }

    [Fact]
    public async Task Me_WithoutToken_ReturnsUnauthorized()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_WithInvalidToken_ReturnsUnauthorized()
    {
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");

        HttpResponseMessage response = await client.GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_ReturnsClaimsFromToken()
    {
        TestUser user = await _api.RegisterAsync(displayName: "Алиса");
        _api.Authorize(user);

        HttpResponseMessage response = await _api.Client.GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement json = await PartyAppApi.ReadJsonAsync(response);
        json.GetProperty("userId").GetString().Should().Be(user.Id.ToString());
        json.GetProperty("username").GetString().Should().Be(user.Username);
        json.GetProperty("displayName").GetString().Should().Be("Алиса");
        json.GetProperty("role").GetString().Should().Be("Player");
    }

    [Fact]
    public async Task Me_ForAdmin_ReturnsAdminRole()
    {
        TestUser admin = await _api.CreateAdminAsync();
        _api.Authorize(admin);

        HttpResponseMessage response = await _api.Client.GetAsync("/api/auth/me");

        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("role").GetString().Should().Be("Admin");
    }

    [Fact]
    public async Task Register_ResponseContentType_IsJson()
    {
        HttpResponseMessage response = await _api.RegisterRawAsync(PartyAppApi.UniqueUsername());

        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
    }
}