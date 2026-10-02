using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Events.Handlers;

/// <summary>
/// Обработчик ивента "dare" (фанты / правда-или-действие).
/// Игрок тянет случайное задание, повторов у одного игрока не бывает.
/// Баллы начисляются сразу — честность на совести компании.
/// </summary>
public class DareHandler : IEventHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPointsAwardService _pointsAward;
    private readonly ILogger<DareHandler> _logger;

    public DareHandler(
        IServiceScopeFactory scopeFactory,
        IPointsAwardService pointsAward,
        ILogger<DareHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _pointsAward = pointsAward;
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

        HashSet<int> used = await GetUsedTaskIndicesAsync(session.Id, playerId, ct);
        List<int> available = Enumerable.Range(0, tasks.Count).Where(i => !used.Contains(i)).ToList();

        if (available.Count == 0)
            return SubmissionResult.Fail("Ты уже выполнил все фанты!");

        int taskIndex = available[Random.Shared.Next(available.Count)];
        string task = tasks[taskIndex];

        await _pointsAward.AwardAsync(
            playerId,
            points,
            $"Фант: {task}",
            sessionId: session.Id,
            ct: ct);

        _logger.LogInformation(
            "Dare: player {PlayerId} got task {TaskIndex} in session {SessionId}",
            playerId, taskIndex, session.Id);

        return SubmissionResult.Ok(
            points,
            "Фант выполнен!",
            new { taskIndex, task, completed = used.Count + 1, total = tasks.Count });
    }

    public async Task<object?> GetPlayerDataAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        CancellationToken ct = default)
    {
        var config = JsonSerializer.Deserialize<DareConfig>(definition.ConfigJson, EventJsonOptions.Default);
        int total = config?.Tasks?.Count ?? 0;

        HashSet<int> used = await GetUsedTaskIndicesAsync(session.Id, playerId, ct);

        return new { completed = used.Count, total };
    }

    private async Task<HashSet<int>> GetUsedTaskIndicesAsync(Guid sessionId, Guid playerId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        List<string> payloads = await db.PlayerSubmissions
            .Where(s => s.SessionId == sessionId && s.PlayerId == playerId)
            .Select(s => s.PayloadJson)
            .ToListAsync(ct);

        var used = new HashSet<int>();

        foreach (string payloadJson in payloads)
        {
            try
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson, EventJsonOptions.Default);
                if (payload.TryGetProperty("taskIndex", out var indexProp) && indexProp.TryGetInt32(out int index))
                    used.Add(index);
            }
            catch { /* ignore */ }
        }

        return used;
    }

    private class DareConfig
    {
        public int Points { get; set; } = 5;
        public List<string> Tasks { get; set; } = new();
    }
}