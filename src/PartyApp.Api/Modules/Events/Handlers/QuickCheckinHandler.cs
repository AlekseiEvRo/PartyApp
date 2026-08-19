using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Events.Handlers;

/// <summary>
/// Обработчик ивента "quick_checkin" (тост за именинника).
/// Игрок нажимает кнопку → получает балл.
/// Cooldown защищает от накруток.
/// </summary>
public class QuickCheckinHandler : IEventHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<QuickCheckinHandler> _logger;

    private readonly SemaphoreSlim _cooldownLock = new(1, 1);
    private DateTime? _cooldownUntilUtc;
    private Guid? _lastPlayerId;
    private string? _lastPlayerName;

    public QuickCheckinHandler(IServiceScopeFactory scopeFactory, ILogger<QuickCheckinHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public string EventType => "quick_checkin";

    public async Task<SubmissionResult> HandleSubmissionAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        string payloadJson,
        CancellationToken ct = default)
    {
        // Читаем конфиг из EventDefinition
        var config = JsonSerializer.Deserialize<QuickCheckinConfig>(definition.ConfigJson);
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
            var nowUtc = DateTime.UtcNow;

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
        await AwardPointsAsync(playerId, points, session.Id, $"Тост за именинника ({definition.DisplayName})", ct);

        _logger.LogInformation(
            "Player {PlayerName} ({PlayerId}) said a toast in session {SessionId}",
            playerName, playerId, session.Id);

        return SubmissionResult.Ok(
            points,
            "Тост засчитан!",
            new { busyUntilUtc = _cooldownUntilUtc.Value, playerName });
    }

    private async Task AwardPointsAsync(Guid userId, int points, Guid? sessionId, string description, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var wallet = await db.Wallets.SingleOrDefaultAsync(w => w.UserId == userId, ct);

        if (wallet is null)
        {
            wallet = new Domain.Entities.Wallet { UserId = userId, Balance = points };
            db.Wallets.Add(wallet);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            wallet.Balance += points;
            await db.SaveChangesAsync(ct);
        }

        var transaction = new WalletTransaction
        {
            WalletId = wallet.Id,
            Amount = points,
            Type = WalletTransactionType.EventReward,
            Description = description,
            RelatedSessionId = sessionId
        };

        db.WalletTransactions.Add(transaction);
        await db.SaveChangesAsync(ct);
    }

    private class QuickCheckinConfig
    {
        public int Points { get; set; } = 1;
        public int CooldownSeconds { get; set; } = 60;
    }
}