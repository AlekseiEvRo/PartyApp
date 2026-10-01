using System.Net;

using FluentAssertions;

using PartyApp.IntegrationTests.Infrastructure;

namespace PartyApp.IntegrationTests.Endpoints;

public class HealthEndpointsTests : IClassFixture<PartyAppFactory>
{
    private readonly PartyAppFactory _factory;

    public HealthEndpointsTests(PartyAppFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Health_ReturnsOk()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PartyAppApi.ReadJsonAsync(response)).GetProperty("status").GetString().Should().Be("ok");
    }
}