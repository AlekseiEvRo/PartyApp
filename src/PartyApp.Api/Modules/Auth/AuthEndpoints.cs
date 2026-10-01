using System.Security.Claims;

using PartyApp.Api.Modules.Auth.Requests;
using PartyApp.Api.Modules.Auth.Services;

namespace PartyApp.Api.Modules.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/register", async (
            RegisterRequest request,
            IAuthService authService,
            CancellationToken ct) =>
        {
            var response = await authService.RegisterAsync(request, ct);
            return Results.Ok(response);
        })
        .RequireRateLimiting("auth");

        group.MapPost("/login", async (
            LoginRequest request,
            IAuthService authService,
            CancellationToken ct) =>
        {
            var response = await authService.LoginAsync(request, ct);
            return Results.Ok(response);
        })
        .RequireRateLimiting("auth");

        group.MapGet("/me", (ClaimsPrincipal user) =>
            {
                return Results.Ok(new
                {
                    UserId = user.FindFirst("sub")?.Value,
                    Username = user.Identity?.Name,
                    DisplayName = user.FindFirst("displayName")?.Value,
                    Role = user.FindFirst("role")?.Value
                });
            })
            .RequireAuthorization();

        return app;
    }
}