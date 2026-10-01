using System.Security.Claims;

using FluentAssertions;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

using PartyApp.Api.Hubs;

namespace PartyApp.UnitTests.Hubs;

public class SubClaimUserIdProviderTests
{
    private readonly SubClaimUserIdProvider _provider = new();

    private static HubConnectionContext CreateConnection(ClaimsPrincipal? user)
    {
        DefaultConnectionContext connection = new();
        connection.Features.Set<IConnectionUserFeature>(new TestUserFeature { User = user });

        return new HubConnectionContext(
            connection,
            new HubConnectionContextOptions(),
            NullLoggerFactory.Instance);
    }

    private sealed class TestUserFeature : IConnectionUserFeature
    {
        public ClaimsPrincipal? User { get; set; }
    }

    [Fact]
    public void GetUserId_ReturnsSubClaim()
    {
        Guid userId = Guid.NewGuid();
        ClaimsPrincipal principal = new(new ClaimsIdentity(new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("name", "alice")
        }));

        _provider.GetUserId(CreateConnection(principal)).Should().Be(userId.ToString());
    }

    [Fact]
    public void GetUserId_WithoutSubClaim_ReturnsNull()
    {
        ClaimsPrincipal principal = new(new ClaimsIdentity(new[]
        {
            new Claim("name", "alice")
        }));

        _provider.GetUserId(CreateConnection(principal)).Should().BeNull();
    }

    [Fact]
    public void GetUserId_WithoutUser_ReturnsNull()
    {
        _provider.GetUserId(CreateConnection(null)).Should().BeNull();
    }
}