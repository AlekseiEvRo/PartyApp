using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PartyApp.Api.Hubs;
using PartyApp.Api.Modules.Achievements;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Events;

/// <summary>
/// Подтверждение фантов админом: после броска фант ждёт проверки,
/// и только подтверждение начисляет баллы игроку.
/// </summary>
public static class DareEndpoints
{
    public static IEndpointRouteBuilder MapDareEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/events/dare")
            .WithTags("Dare")
            .RequireAuthorization("AdminOnly");

        // Очередь фантов, которые ждут подтверждения
        group.MapGet("/pending", async (AppDbContext db, CancellationToken ct) =>
        {
            var pending = await db.DareAssignments.AsNoTracking()
                .Where(a => a.Status == DareStatus.Pending)
                .OrderBy(a => a.CreatedAt)
                .Select(a => new
                {
                    a.Id,
                    a.SessionId,
                    SessionName = a.Session.Definition.DisplayName,
                    PlayerName = a.Player.DisplayName,
                    a.Task,
                    a.Points,
                    a.CreatedAt
                })
                .ToListAsync(ct);

            return Results.Ok(pending);
        });

        group.MapPost("/{assignmentId:guid}/confirm", async (
                Guid assignmentId,
                AppDbContext db,
                IPointsAwardService pointsAward,
                IHubContext<PartyHub> hub,
                AchievementService achievements,
                CancellationToken ct) =>
            {
                DareAssignment? assignment = await db.DareAssignments
                    .Include(a => a.Player)
                    .SingleOrDefaultAsync(a => a.Id == assignmentId, ct);

                if (assignment is null)
                    return Results.NotFound(new { error = "Фант не найден" });

                if (assignment.Status != DareStatus.Pending)
                    return Results.Conflict(new { error = "Фант уже подтверждён" });

                // Сначала фиксируем статус, чтобы повторное подтверждение не начислило баллы дважды
                assignment.Status = DareStatus.Confirmed;
                assignment.ConfirmedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);

                await pointsAward.AwardAsync(
                    assignment.PlayerId,
                    assignment.Points,
                    $"Фант: {assignment.Task}",
                    ct: ct);

                // Игрок сразу видит, что баллы начислены
                await hub.Clients.All.SendAsync("DareConfirmed", new
                {
                    sessionId = assignment.SessionId,
                    playerId = assignment.PlayerId,
                    points = assignment.Points
                }, ct);

                await achievements.EvaluatePlayerAsync(assignment.PlayerId, ct);

                return Results.Ok(new
                {
                    assignment.Id,
                    playerName = assignment.Player.DisplayName,
                    points = assignment.Points
                });
            });

        return app;
    }
}