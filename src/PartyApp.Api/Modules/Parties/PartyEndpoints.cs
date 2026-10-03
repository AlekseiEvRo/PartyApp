using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using PartyApp.Api.Modules.Admin;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Parties;

/// <summary>
/// Вечеринки: список и итоги, старт новой смены, завершение и сценарий ивентов.
/// </summary>
public static class PartyEndpoints
{
    private const int MaxNameLength = 100;

    public static IEndpointRouteBuilder MapPartyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/parties")
            .WithTags("Parties")
            .RequireAuthorization();

        // Список вечеринок со счётчиками ивентов и фото (админ)
        group.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            var parties = await db.Parties.AsNoTracking()
                .OrderByDescending(p => p.StartedAt)
                .Select(p => new
                {
                    p.Id,
                    p.Name,
                    Status = p.Status.ToString(),
                    p.StartedAt,
                    p.EndedAt,
                    p.CreatedAt
                })
                .ToListAsync(ct);

            Dictionary<Guid, int> sessionCounts = await db.EventSessions.AsNoTracking()
                .Where(s => s.PartyId != null)
                .GroupBy(s => s.PartyId!.Value)
                .Select(g => new { PartyId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.PartyId, x => x.Count, ct);

            Dictionary<Guid, int> photoCounts = await db.PartyPhotos.AsNoTracking()
                .Where(p => p.PartyId != null)
                .GroupBy(p => p.PartyId!.Value)
                .Select(g => new { PartyId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.PartyId, x => x.Count, ct);

            var result = parties.Select(p => new
            {
                p.Id,
                p.Name,
                p.Status,
                p.StartedAt,
                p.EndedAt,
                p.CreatedAt,
                SessionsCount = sessionCounts.GetValueOrDefault(p.Id),
                PhotosCount = photoCounts.GetValueOrDefault(p.Id)
            });

            return Results.Ok(result);
        })
        .RequireAuthorization("AdminOnly");

        // Текущая вечеринка (или последняя завершённая) с итогами — для экрана и админки
        group.MapGet("/current", async (PartyService parties, CancellationToken ct) =>
        {
            Party? current = await parties.GetCurrentOrLatestAsync(ct);
            if (current is null)
                return Results.Ok(new { summary = (object?)null, nextScheduleItem = (object?)null });

            PartySummaryDto? summary = await parties.GetSummaryAsync(current.Id, ct);
            ScheduleItemDto? next = current.Status == PartyStatus.Active
                ? await parties.GetNextScheduleItemAsync(current.Id, ct)
                : null;

            return Results.Ok(new { summary, nextScheduleItem = next });
        });

        group.MapGet("/{partyId:guid}/summary", async (
            Guid partyId,
            PartyService parties,
            CancellationToken ct) =>
        {
            PartySummaryDto? summary = await parties.GetSummaryAsync(partyId, ct);
            return summary is null
                ? Results.NotFound(new { error = "Вечеринка не найдена" })
                : Results.Ok(summary);
        });

        // Начать новую вечеринку: предыдущая завершается, активные ивенты закрываются,
        // при resetBalances баллы всех игроков обнуляются (история остаётся в транзакциях)
        group.MapPost("/", async (
            StartPartyRequest request,
            ClaimsPrincipal user,
            PartyService parties,
            AppDbContext db,
            AdminAuditService audit,
            CancellationToken ct) =>
        {
            string name = request.Name?.Trim() ?? string.Empty;
            if (name.Length == 0)
                return Results.BadRequest(new { error = "Название не может быть пустым" });

            if (name.Length > MaxNameLength)
                return Results.BadRequest(new { error = $"Название не длиннее {MaxNameLength} символов" });

            bool resetBalances = request.ResetBalances ?? true;
            Party party = await parties.StartAsync(name, resetBalances, ct);

            audit.Record(
                GetUserId(user),
                "party_start",
                null,
                resetBalances ? $"{name} · баллы обнулены" : name);
            await db.SaveChangesAsync(ct);

            return Results.Ok(PartyService.ToDto(party));
        })
        .RequireAuthorization("AdminOnly");

        group.MapPost("/{partyId:guid}/finish", async (
            Guid partyId,
            ClaimsPrincipal user,
            PartyService parties,
            AppDbContext db,
            AdminAuditService audit,
            CancellationToken ct) =>
        {
            PartySummaryDto? summary = await parties.FinishAsync(partyId, ct);
            if (summary is null)
                return Results.NotFound(new { error = "Вечеринка не найдена" });

            audit.Record(GetUserId(user), "party_finish", null, summary.Party.Name);
            await db.SaveChangesAsync(ct);

            return Results.Ok(summary);
        })
        .RequireAuthorization("AdminOnly");

        // === Сценарий вечеринки ===

        group.MapGet("/{partyId:guid}/schedule", async (
            Guid partyId,
            PartyService parties,
            AppDbContext db,
            CancellationToken ct) =>
        {
            bool exists = await db.Parties.AnyAsync(p => p.Id == partyId, ct);
            if (!exists)
                return Results.NotFound(new { error = "Вечеринка не найдена" });

            return Results.Ok(await parties.GetScheduleAsync(partyId, ct));
        });

        group.MapPost("/{partyId:guid}/schedule", async (
            Guid partyId,
            AddScheduleItemRequest request,
            PartyService parties,
            AppDbContext db,
            CancellationToken ct) =>
        {
            Party? party = await db.Parties.AsNoTracking().FirstOrDefaultAsync(p => p.Id == partyId, ct);
            if (party is null)
                return Results.NotFound(new { error = "Вечеринка не найдена" });

            if (party.Status != PartyStatus.Active)
                return Results.Conflict(new { error = "Вечеринка уже завершена" });

            EventDefinition? definition = await db.EventDefinitions.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == request.DefinitionId, ct);
            if (definition is null)
                return Results.NotFound(new { error = "Ивент не найден" });

            if (!definition.IsActive)
                return Results.Conflict(new { error = "Ивент выключен" });

            ScheduleItemDto item = await parties.AddScheduleItemAsync(partyId, definition, ct);
            return Results.Ok(item);
        })
        .RequireAuthorization("AdminOnly");

        group.MapPost("/{partyId:guid}/schedule/{itemId:guid}/move", async (
            Guid partyId,
            Guid itemId,
            MoveScheduleItemRequest request,
            PartyService parties,
            CancellationToken ct) =>
        {
            bool moved = await parties.MoveScheduleItemAsync(partyId, itemId, request.Up, ct);
            return moved
                ? Results.Ok(new { success = true })
                : Results.NotFound(new { error = "Пункт сценария не найден" });
        })
        .RequireAuthorization("AdminOnly");

        group.MapDelete("/{partyId:guid}/schedule/{itemId:guid}", async (
            Guid partyId,
            Guid itemId,
            PartyService parties,
            CancellationToken ct) =>
        {
            bool removed = await parties.RemoveScheduleItemAsync(partyId, itemId, ct);
            return removed
                ? Results.Ok(new { success = true })
                : Results.NotFound(new { error = "Пункт сценария не найден" });
        })
        .RequireAuthorization("AdminOnly");

        group.MapPost("/{partyId:guid}/schedule/{itemId:guid}/start", async (
            Guid partyId,
            Guid itemId,
            ClaimsPrincipal user,
            PartyService parties,
            CancellationToken ct) =>
        {
            ScheduleStartOutcome outcome = await parties.StartScheduleItemAsync(partyId, itemId, GetUserId(user), ct);
            if (outcome.Error is not null)
                return Results.Conflict(new { error = outcome.Error });

            return Results.Ok(new { sessionId = outcome.SessionId });
        })
        .RequireAuthorization("AdminOnly");

        return app;
    }

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        return Guid.Parse(user.FindFirst("sub")!.Value);
    }
}

public record StartPartyRequest(string? Name, bool? ResetBalances);

public record AddScheduleItemRequest(Guid DefinitionId);

public record MoveScheduleItemRequest(bool Up);
