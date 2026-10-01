using System.IdentityModel.Tokens.Jwt;
using System.Text;

using FluentAssertions;

using Microsoft.IdentityModel.Tokens;

using PartyApp.Api.Modules.Auth.Services;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Auth;

public class TokenServiceTests
{
    private const string SigningKey = "TestSigningKeyThatIsLongEnoughForHmacSha256_42";
    private const string Issuer = "test-issuer";
    private const string Audience = "test-audience";

    private static TokenService CreateService(params (string Key, string? Value)[] overrides)
    {
        Dictionary<string, string?> values = new()
        {
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience,
            ["Jwt:SigningKey"] = SigningKey,
            ["Jwt:AccessTokenLifetimeMinutes"] = "60"
        };

        foreach ((string key, string? value) in overrides)
        {
            if (value is null)
                values.Remove(key);
            else
                values[key] = value;
        }

        return new TokenService(TestConfiguration.Create(values.Select(v => (v.Key, v.Value)).ToArray()));
    }

    private static TokenValidationParameters CreateValidationParameters(string signingKey = SigningKey)
    {
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = Issuer,
            ValidAudience = Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey))
        };
    }

    [Fact]
    public void GenerateToken_ContainsIdentityClaims()
    {
        Guid userId = Guid.NewGuid();

        (string token, _) = CreateService().GenerateToken(userId, "alice", "Алиса", "Admin");

        JwtSecurityToken jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Claims.Should().Contain(c => c.Type == "sub" && c.Value == userId.ToString());
        jwt.Claims.Should().Contain(c => c.Type == "name" && c.Value == "alice");
        jwt.Claims.Should().Contain(c => c.Type == "displayName" && c.Value == "Алиса");
        jwt.Claims.Should().Contain(c => c.Type == "role" && c.Value == "Admin");
    }

    [Fact]
    public void GenerateToken_ContainsSingleUniqueJti()
    {
        TokenService service = CreateService();

        (string first, _) = service.GenerateToken(Guid.NewGuid(), "alice", "Алиса", "Player");
        (string second, _) = service.GenerateToken(Guid.NewGuid(), "alice", "Алиса", "Player");

        List<string> jtis = new JwtSecurityTokenHandler().ReadJwtToken(first).Claims
            .Where(c => c.Type == JwtRegisteredClaimNames.Jti)
            .Select(c => c.Value)
            .ToList();

        jtis.Should().ContainSingle();
        new JwtSecurityTokenHandler().ReadJwtToken(second).Claims
            .Should().Contain(c => c.Type == JwtRegisteredClaimNames.Jti && c.Value != jtis[0]);
        first.Should().NotBe(second);
    }

    [Fact]
    public void GenerateToken_UsesConfiguredIssuerAudienceAndLifetime()
    {
        DateTime before = DateTime.UtcNow;

        (string token, DateTime expiresAtUtc) = CreateService().GenerateToken(Guid.NewGuid(), "bob", "Боб", "Player");

        JwtSecurityToken jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Issuer.Should().Be(Issuer);
        jwt.Audiences.Should().ContainSingle().Which.Should().Be(Audience);
        expiresAtUtc.Should().BeCloseTo(before.AddMinutes(60), TimeSpan.FromSeconds(10));
        jwt.ValidTo.Should().BeCloseTo(expiresAtUtc, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void GenerateToken_UsesCustomLifetime()
    {
        DateTime before = DateTime.UtcNow;

        (_, DateTime expiresAtUtc) = CreateService(("Jwt:AccessTokenLifetimeMinutes", "5"))
            .GenerateToken(Guid.NewGuid(), "bob", "Боб", "Player");

        expiresAtUtc.Should().BeCloseTo(before.AddMinutes(5), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void GenerateToken_WithoutOptionalSettings_FallsBackToDefaults()
    {
        TokenService service = CreateService(
            ("Jwt:Issuer", null),
            ("Jwt:Audience", null),
            ("Jwt:AccessTokenLifetimeMinutes", null));

        (string token, DateTime expiresAtUtc) = service.GenerateToken(Guid.NewGuid(), "bob", "Боб", "Player");

        JwtSecurityToken jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Issuer.Should().Be("party-app");
        jwt.Audiences.Should().ContainSingle().Which.Should().Be("party-app-clients");
        expiresAtUtc.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(60), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void GenerateToken_WithoutSigningKey_Throws()
    {
        TokenService service = CreateService(("Jwt:SigningKey", null));

        Action act = () => service.GenerateToken(Guid.NewGuid(), "bob", "Боб", "Player");

        act.Should().Throw<InvalidOperationException>().WithMessage("*SigningKey*");
    }

    [Fact]
    public void GenerateToken_ProducesTokenAcceptedByMatchingValidationParameters()
    {
        (string token, _) = CreateService().GenerateToken(Guid.NewGuid(), "carol", "Кэрол", "Player");

        JwtSecurityTokenHandler handler = new();

        Action act = () => handler.ValidateToken(token, CreateValidationParameters(), out _);

        act.Should().NotThrow();
    }

    [Fact]
    public void GenerateToken_ProducesTokenRejectedByDifferentKey()
    {
        (string token, _) = CreateService().GenerateToken(Guid.NewGuid(), "carol", "Кэрол", "Player");
        string otherKey = "AnotherSigningKeyThatIsLongEnoughForHmacSha256!!";

        JwtSecurityTokenHandler handler = new();

        Action act = () => handler.ValidateToken(token, CreateValidationParameters(otherKey), out _);

        act.Should().Throw<SecurityTokenException>();
    }

    [Fact]
    public void GenerateToken_SubClaimIsParseableGuid()
    {
        Guid userId = Guid.NewGuid();

        (string token, _) = CreateService().GenerateToken(userId, "dave", "Дэйв", "Player");

        string sub = new JwtSecurityTokenHandler().ReadJwtToken(token).Claims
            .Single(c => c.Type == "sub").Value;

        Guid.TryParse(sub, out Guid parsed).Should().BeTrue();
        parsed.Should().Be(userId);
    }
}