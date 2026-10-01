using System.Text.Json;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;

namespace PartyApp.Api.Modules.Events.Handlers;

/// <summary>
/// Обработчик ивента "quick_checkin" (тост за именинника).
/// Игрок нажимает кнопку → получает балл.
/// Cooldown защищает от накруток.
/// </summary>
public class QuickCheckinHandler : IEventHandler
{
    private readonly IPointsAwardService _pointsAward;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<QuickCheckinHandler> _logger;

    private readonly SemaphoreSlim _cooldownLock = new(1, 1);
    private DateTime? _cooldownUntilUtc;
    private Guid? _lastPlayerId;
    private string? _lastPlayerName;

    public QuickCheckinHandler(
        IPointsAwardService pointsAward,
        TimeProvider timeProvider,
        ILogger<QuickCheckinHandler> logger)
    {
        _pointsAward = pointsAward;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public string EventType => "quick_checkin";

    public string DefaultConfigJson => """{"points":1,"cooldownSeconds":60}""";

    public async Task<SubmissionResult> HandleSubmissionAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        string payloadJson,
        CancellationToken ct = default)
    {
        // Читаем конфиг из EventDefinition
        var config = JsonSerializer.Deserialize<QuickCheckinConfig>(definition.ConfigJson, EventJsonOptions.Default);
        var cooldownSeconds = config?.CooldownSeconds ?? 60;
        var points = config?.Points ?? 1;

        // Извлекаем имя игрока из payload (клиент передаёт)
        string? playerName = null;
        if (!string.IsNullOrEmpty(payloadJson))
        {
            try
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson);
                playerName = payload.TryGetProperty("playerName", out var nameProp) ? nameProp.GetString() : null;
            }
            catch { /* ignore */ }
        }

        await _cooldownLock.WaitAsync(ct);
        try
        {
            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

            if (_cooldownUntilUtc.HasValue && nowUtc < _cooldownUntilUtc.Value)
            {
                return SubmissionResult.Fail(
                    $"Сейчас говорит тост {_lastPlayerName}. Подожди {_cooldownUntilUtc.Value - nowUtc:ss} сек.",
                    new { busyUntilUtc = _cooldownUntilUtc.Value, busyByName = _lastPlayerName });
            }

            _cooldownUntilUtc = nowUtc.AddSeconds(cooldownSeconds);
            _lastPlayerId = playerId;
            _lastPlayerName = playerName;
        }
        finally
        {
            _cooldownLock.Release();
        }

        // Начисляем баллы
        await _pointsAward.AwardAsync(
            playerId,
            points,
            $"Тост за именинника ({definition.DisplayName})",
            sessionId: session.Id,
            ct: ct);

        _logger.LogInformation(
            "Player {PlayerName} ({PlayerId}) said a toast in session {SessionId}",
            playerName, playerId, session.Id);

        return SubmissionResult.Ok(
            points,
            "Тост засчитан!",
            new { busyUntilUtc = _cooldownUntilUtc.Value, playerName });
    }

    private class QuickCheckinConfig
    {
        public int Points { get; set; } = 1;
        public int CooldownSeconds { get; set; } = 60;
    }
}