namespace PartyApp.Api.Modules.Auth.Services;

public interface ITokenService
{
    (string Token, DateTime ExpiresAtUtc) GenerateToken(
        Guid userId,
        string username,
        string displayName,
        string role,
        string securityStamp);
}