using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using PartyApp.Domain.Entities;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Polls;

/// <summary>
/// Голосование за следующий ивент: игроки голосуют, админ закрывает
/// и запускает победителя.
/// </summary>
public static class PollEndpoints
{
    private const int MaxQuestionLength = 200;
    private const int MinOptions = 2;
    private const int MaxOptions = 8;

    public static IEndpointRouteBuilder MapPollEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/polls")
            .WithTags("Polls")
            .RequireAuthorization();

        // Текущее (последнее) голосование вместе с моим выбором
        group.MapGet("/current", async (ClaimsPrincipal user, PollService polls, CancellationToken ct) =>
        {
            Guid? userId = Guid.TryParse(user.FindFirst("sub")?.Value, out Guid parsed) ? parsed : null;
            return Results.Ok(new { poll = await polls.GetCurrentAsync(userId, ct) });
        });

        group.MapPost("/{pollId:guid}/vote", async (
            Guid pollId,
            VoteRequest request,
            ClaimsPrincipal user,
            PollService polls,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(user.FindFirst("sub")?.Value, out Guid userId))
                return Results.Unauthorized();

            (PollDto? poll, string? error) = await polls.VoteAsync(pollId, userId, request.OptionId, ct);
            return error is null
                ? Results.Ok(poll)
                : Results.Conflict(new { error });
        })
        .RequireRateLimiting("submit");

        group.MapPost("/", async (
            CreatePollRequest request,
            ClaimsPrincipal user,
            PollService polls,
            AppDbContext db,
            CancellationToken ct) =>
        {
            string question = request.Question?.Trim() ?? string.Empty;
            if (question.Length == 0)
                return Results.BadRequest(new { error = "Вопрос не может быть пустым" });

            if (question.Length > MaxQuestionLength)
                return Results.BadRequest(new { error = $"Вопрос не длиннее {MaxQuestionLength} символов" });

            List<Guid> definitionIds = (request.DefinitionIds ?? new List<Guid>())
                .Distinct()
                .ToList();

            if (definitionIds.Count is < MinOptions or > MaxOptions)
                return Results.BadRequest(new { error = $"Нужно от {MinOptions} до {MaxOptions} вариантов" });

            List<EventDefinition> definitions = await db.EventDefinitions.AsNoTracking()
                .Where(d => definitionIds.Contains(d.Id) && d.IsActive)
                .ToListAsync(ct);

            if (definitions.Count != definitionIds.Count)
                return Results.BadRequest(new { error = "Некоторые ивенты не найдены или выключены" });

            Guid adminId = Guid.Parse(user.FindFirst("sub")!.Value);
            PollDto poll = await polls.CreateAsync(question, definitionIds, adminId, ct);

            return Results.Ok(poll);
        })
        .RequireAuthorization("AdminOnly");

        group.MapPost("/{pollId:guid}/close", async (
            Guid pollId,
            PollService polls,
            CancellationToken ct) =>
        {
            (PollDto? poll, string? error) = await polls.CloseAsync(pollId, ct);
            return error is null
                ? Results.Ok(poll)
                : Results.NotFound(new { error });
        })
        .RequireAuthorization("AdminOnly");

        // Запустить победивший ивент одной кнопкой
        group.MapPost("/{pollId:guid}/start-winner", async (
            Guid pollId,
            ClaimsPrincipal user,
            PollService polls,
            CancellationToken ct) =>
        {
            Guid adminId = Guid.Parse(user.FindFirst("sub")!.Value);
            (Guid? sessionId, string? error) = await polls.StartWinnerAsync(pollId, adminId, ct);

            return error is null
                ? Results.Ok(new { sessionId })
                : Results.Conflict(new { error });
        })
        .RequireAuthorization("AdminOnly");

        return app;
    }
}

public record VoteRequest(Guid OptionId);

public record CreatePollRequest(string? Question, List<Guid>? DefinitionIds);
