using PartyApp.Api.Modules.Auth.Requests;
using PartyApp.Api.Modules.Auth.Responces;

namespace PartyApp.Api.Modules.Auth.Services;

public interface IAuthService
{
    Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default);
    Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default);
}