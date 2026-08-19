using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace PartyApp.Api.Modules.Toast;

public static class ToastEndpoints
{
    public static IEndpointRouteBuilder MapToastEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/toast")
            .WithTags("Toast")
            .RequireAuthorization();

        group.MapPost("/say", async (ClaimsPrincipal user, ToastService toastService, CancellationToken ct) =>
        {
            var subClaim = user.FindFirst("sub")?.Value;
            if (subClaim is null || !Guid.TryParse(subClaim, out var userId))
            {
                return Results.Unauthorized();
            }

            var username = user.FindFirst("name")?.Value ?? "Unknown";

            var result = await toastService.TrySayToastAsync(userId, username, ct);

            if (!result.Success)
            {
                return Results.Conflict(new
                {
                    error = result.Message,
                    busyUntilUtc = result.BusyUntilUtc,
                    busyByName = result.BusyByName
                });
            }

            return Results.Ok(new
            {
                message = result.Message,
                pointsAwarded = result.Points,
                busyUntilUtc = result.BusyUntilUtc
            });
        });

        group.MapGet("/status", (ToastService toastService) =>
        {
            var status = toastService.GetStatus();
            return Results.Ok(status);
        });

        return app;
    }
}