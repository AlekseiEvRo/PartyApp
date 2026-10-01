using PartyApp.Api.Modules.Events.Services;

namespace PartyApp.Api.Modules.Qr;

public static class QrEndpoints
{
    public static IEndpointRouteBuilder MapQrEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/qr")
            .WithTags("QR Codes")
            .RequireAuthorization("AdminOnly");

        group.MapGet("/tokens", async (IQrTokenService qrService, CancellationToken ct) =>
        {
            var tokens = await qrService.GetTokensAsync(ct);
            return Results.Ok(tokens);
        });

        group.MapPost("/tokens/generate", async (
            GenerateQrRequest request,
            IQrTokenService qrService,
            CancellationToken ct) =>
        {
            if (request.Count < 1 || request.Count > 100)
                return Results.BadRequest(new { error = "Количество токенов должно быть от 1 до 100" });

            if (request.Points <= 0)
                return Results.BadRequest(new { error = "Количество баллов должно быть больше 0" });

            var tokens = await qrService.GenerateTokensAsync(request.Count, request.Points, ct);
            return Results.Ok(new { generated = tokens.Count, tokens });
        });

        return app;
    }
}

public record GenerateQrRequest(int Count, int Points);