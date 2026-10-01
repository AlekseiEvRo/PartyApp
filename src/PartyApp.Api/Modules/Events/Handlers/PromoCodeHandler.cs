using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Events.Handlers;

/// <summary>
/// Обработчик ивента "promo_code".
/// Именинник называет код вслух, игроки вводят его в приложении.
/// Каждый код можно активировать один раз на игрока.
/// </summary>
public class PromoCodeHandler : IEventHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PointsAwardService _pointsAward;
    private readonly ILogger<PromoCodeHandler> _logger;

    public PromoCodeHandler(
        IServiceScopeFactory scopeFactory,
        PointsAwardService pointsAward,
        ILogger<PromoCodeHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _pointsAward = pointsAward;
        _logger = logger;
    }

    public string EventType => "promo_code";

    public string DefaultConfigJson => """
        {
            "codes": ["КОД1", "КОД2"],
            "pointsPerCode": 15,
            "oneTimePerPlayer": true
        }
        """;

    public async Task<SubmissionResult> HandleSubmissionAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        string payloadJson,
        CancellationToken ct = default)
    {
        // Читаем конфиг
        var config = JsonSerializer.Deserialize<PromoCodeConfig>(definition.ConfigJson, EventJsonOptions.Default);
        if (config is null || config.Codes is null || config.Codes.Count == 0)
            return SubmissionResult.Fail("Промокоды не настроены");

        var pointsPerCode = config.PointsPerCode > 0 ? config.PointsPerCode : 15;
        var oneTimePerPlayer = config.OneTimePerPlayer;

        // Извлекаем код из payload
        string? submittedCode = null;
        if (!string.IsNullOrEmpty(payloadJson))
        {
            try
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson, EventJsonOptions.Default);
                submittedCode = payload.TryGetProperty("code", out var codeProp) ? codeProp.GetString()?.Trim() : null;
            }
            catch { /* ignore */ }
        }

        if (string.IsNullOrWhiteSpace(submittedCode))
            return SubmissionResult.Fail("Введи промокод");

        // Проверяем, есть ли такой код в списке (регистронезависимо)
        var matchedCode = config.Codes.FirstOrDefault(c =>
            string.Equals(c.Trim(), submittedCode, StringComparison.OrdinalIgnoreCase));

        if (matchedCode is null)
            return SubmissionResult.Fail("Неверный промокод");

        // Проверяем, не использовал ли игрок этот код уже
        if (oneTimePerPlayer)
        {
            var alreadyUsed = await HasPlayerUsedCodeAsync(session.Id, playerId, matchedCode, ct);
            if (alreadyUsed)
                return SubmissionResult.Fail("Ты уже активировал этот код");
        }

        // Начисляем баллы
        await _pointsAward.AwardAsync(
            playerId,
            pointsPerCode,
            $"Промокод: {matchedCode}",
            sessionId: session.Id,
            ct: ct);

        _logger.LogInformation(
            "Player {PlayerId} activated promo code {Code} in session {SessionId}",
            playerId, matchedCode, session.Id);

        return SubmissionResult.Ok(
            pointsPerCode,
            $"Промокод «{matchedCode}» активирован!",
            new { code = matchedCode, points = pointsPerCode });
    }

    private async Task<bool> HasPlayerUsedCodeAsync(Guid sessionId, Guid playerId, string code, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Ищем сабмиты этого игрока в этой сессии
        var playerSubmissions = await db.PlayerSubmissions
            .Where(s => s.SessionId == sessionId && s.PlayerId == playerId)
            .ToListAsync(ct);

        // Проверяем, есть ли среди них такой же код
        foreach (var submission in playerSubmissions)
        {   
            try
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(submission.PayloadJson, EventJsonOptions.Default);
                var storedCode = payload.TryGetProperty("code", out var codeProp) ? codeProp.GetString() : null;

                if (string.Equals(storedCode, code, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { /* ignore */ }
        }

        return false;
    }

    private class PromoCodeConfig
    {
        public List<string> Codes { get; set; } = new();
        public int PointsPerCode { get; set; } = 15;
        public bool OneTimePerPlayer { get; set; } = true;
    }
}