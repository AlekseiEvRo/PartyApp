namespace PartyApp.Api.Modules.Auth.Responces;

public record AuthResponse(
    string AccessToken,
    DateTime ExpiresAtUtc,
    Guid UserId,
    string Username,
    string DisplayName,
    string Role);