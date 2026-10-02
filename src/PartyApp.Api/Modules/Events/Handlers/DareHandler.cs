using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Events.Handlers;

/// <summary>
/// Обработчик ивента "dare" (фанты / правда-или-действие).
/// У игрока ровно один фант за сессию. Баллы не начисляются сразу:
/// фант ждёт подтверждения админом (см. DareEndpoints).
/// </summary>
public class DareHandler : IEventHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DareHandler> _logger;

    public DareHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<DareHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public string EventType => "dare";

    public string DefaultConfigJson => """
        {
            "points": 5,
            "tasks": [
                "Скажи тост без слов — только жестами",
                "Спой припев любимой песни именинника",
                "Расскажи смешную историю про именинника",
                "Изобрази любое животное, пока не угадают",
                "Сделай комплимент каждому за столом",
                "Покажи танец на 15 секунд",
                "Придумай новое прозвище имениннику",
                "Скажи скороговорку три раза подряд без ошибок",
                "Признайся в самой нелепой покупке в жизни",
                "Назови пять причин, почему именинник крут"
            ]
        }
        """;

    public async Task<SubmissionResult> HandleSubmissionAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        string payloadJson,
        CancellationToken ct = default)
    {
        var config = JsonSerializer.Deserialize<DareConfig>(definition.ConfigJson, EventJsonOptions.Default);
        var tasks = config?.Tasks ?? new List<string>();
        int points = config?.Points > 0 ? config.Points : 5;

        if (tasks.Count == 0)
            return SubmissionResult.Fail("Фанты не настроены");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        DareAssignment? existing = await db.DareAssignments
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.SessionId == session.Id && a.PlayerId == playerId, ct);

        if (existing is not null)
        {
            string message = existing.Status == DareStatus.Pending
                ? "Фант уже выдан — жди подтверждения"
                : "Ты уже получил свой фант";

            return SubmissionResult.Fail(message, BuildDareData(existing));
        }

        // Стараемся не повторять задания между игроками
        List<int> usedIndices = await db.DareAssignments
            .Where(a => a.SessionId == session.Id)
            .Select(a => a.TaskIndex)
            .ToListAsync(ct);

        List<int> available = Enumerable.Range(0, tasks.Count).Where(i => !usedIndices.Contains(i)).ToList();
        List<int> pool = available.Count > 0 ? available : Enumerable.Range(0, tasks.Count).ToList();

        int taskIndex = pool[Random.Shared.Next(pool.Count)];

        var assignment = new DareAssignment
        {
            SessionId = session.Id,
            PlayerId = playerId,
            TaskIndex = taskIndex,
            Task = tasks[taskIndex],
            Points = points,
            Status = DareStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        db.DareAssignments.Add(assignment);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Dare assigned: player {PlayerId}, task {TaskIndex}, session {SessionId}",
            playerId, taskIndex, session.Id);

        return SubmissionResult.Ok(
            0,
            "Фант выдан! Жди подтверждения",
            BuildDareData(assignment));
    }

    public async Task<object?> GetLiveDataAsync(
        EventSession session,
        EventDefinition definition,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        int pending = await db.DareAssignments
            .CountAsync(a => a.SessionId == session.Id && a.Status == DareStatus.Pending, ct);
        int confirmed = await db.DareAssignments
            .CountAsync(a => a.SessionId == session.Id && a.Status == DareStatus.Confirmed, ct);

        return new { pending, confirmed };
    }

    public async Task<object?> GetPlayerDataAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        DareAssignment? assignment = await db.DareAssignments
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.SessionId == session.Id && a.PlayerId == playerId, ct);

        object? dare = assignment is null ? null : BuildDareData(assignment);

        return new { dare };
    }

    private static object BuildDareData(DareAssignment assignment) => new
    {
        task = assignment.Task,
        taskIndex = assignment.TaskIndex,
        status = assignment.Status == DareStatus.Confirmed ? "confirmed" : "pending",
        points = assignment.Points
    };

    private class DareConfig
    {
        public int Points { get; set; } = 5;
        public List<string> Tasks { get; set; } = new();
    }
}