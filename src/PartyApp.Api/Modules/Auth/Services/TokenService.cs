using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

using Microsoft.IdentityModel.Tokens;

namespace PartyApp.Api.Modules.Auth.Services;

public class TokenService : ITokenService
{
    private readonly IConfiguration _configuration;

    public TokenService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public (string Token, DateTime ExpiresAtUtc) GenerateToken(
        Guid userId,
        string username,
        string displayName,
        string role)
    {
        var issuer = _configuration["Jwt:Issuer"] ?? "party-app";
        var audience = _configuration["Jwt:Audience"] ?? "party-app-clients";
        var signingKey = _configuration["Jwt:SigningKey"]
                         ?? throw new InvalidOperationException("Jwt:SigningKey is not configured");
        var lifetimeMinutes = int.Parse(_configuration["Jwt:AccessTokenLifetimeMinutes"] ?? "60");

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var nowUtc = DateTime.UtcNow;
        var expiresAtUtc = nowUtc.AddMinutes(lifetimeMinutes);

        var claims = new List<Claim>
        {
            new("sub", userId.ToString()),
            new("name", username),
            new("displayName", displayName),
            new("role", role),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: nowUtc,
            expires: expiresAtUtc,
            signingCredentials: credentials);

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

        return (tokenString, expiresAtUtc);
    }
}