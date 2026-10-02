using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Events.Handlers;

/// <summary>
/// Обработчик ивента "predictions" (предсказания имениннику).
/// Игрок отправляет одно предсказание и получает баллы за участие.
/// Чужие предсказания раскрываются только после завершения сессии.
/// </summary>
public class PredictionsHandler : IEventHandler
{
    private const int MaxTextLength = 200;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPointsAwardService _pointsAward;
    private readonly ILogger<PredictionsHandler> _logger;

    public PredictionsHandler(
        IServiceScopeFactory scopeFactory,
        IPointsAwardService pointsAward,
        ILogger<PredictionsHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _pointsAward = pointsAward;
        _logger = logger;
    }

    public string EventType => "predictions";

    public string DefaultConfigJson => """{"points":3,"prompt":"Что случится на вечеринке?"}""";

    public async Task<SubmissionResult> HandleSubmissionAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        string payloadJson,
        CancellationToken ct = default)
    {
        var config = JsonSerializer.Deserialize<PredictionsConfig>(definition.ConfigJson, EventJsonOptions.Default);
        int points = config?.Points > 0 ? config.Points : 3;

        string? text = null;
        if (!string.IsNullOrEmpty(payloadJson))
        {
            try
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson, EventJsonOptions.Default);
                text = payload.TryGetProperty("text", out var textProp) ? textProp.GetString()?.Trim() : null;
            }
            catch { /* ignore */ }
        }

        if (string.IsNullOrWhiteSpace(text))
            return SubmissionResult.Fail("Напиши предсказание");

        if (text.Length > MaxTextLength)
            text = text[..MaxTextLength];

        string? existing = await GetOwnPredictionAsync(session.Id, playerId, ct);
        if (existing is not null)
            return SubmissionResult.Fail("Ты уже отправил предсказание", new { text = existing });

        await _pointsAward.AwardAsync(
            playerId,
            points,
            "Предсказание для именинника",
            sessionId: session.Id,
            ct: ct);

        _logger.LogInformation(
            "Prediction submitted: player {PlayerId} in session {SessionId}",
            playerId, session.Id);

        return SubmissionResult.Ok(points, "Предсказание принято! 🔮", new { text });
    }

    public async Task<object?> GetLiveDataAsync(
        EventSession session,
        EventDefinition definition,
        CancellationToken ct = default)
    {
        var config = JsonSerializer.Deserialize<PredictionsConfig>(definition.ConfigJson, EventJsonOptions.Default);
        List<PredictionRow> rows = await GetPredictionRowsAsync(session.Id, ct);

        // Чужие предсказания показываем только после завершения ивента
        object? revealed = session.State == EventSessionState.Finished
            ? rows.Select(r => new { playerName = r.PlayerName, text = r.Text }).ToArray()
            : null;

        return new
        {
            count = rows.Count,
            prompt = config?.Prompt ?? "Что случится на вечеринке?",
            revealed
        };
    }

    public async Task<object?> GetPlayerDataAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        CancellationToken ct = default)
    {
        string? own = await GetOwnPredictionAsync(session.Id, playerId, ct);

        return new { text = own };
    }

    private async Task<string?> GetOwnPredictionAsync(Guid sessionId, Guid playerId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        string? payloadJson = await db.PlayerSubmissions
            .Where(s => s.SessionId == sessionId && s.PlayerId == playerId)
            .OrderBy(s => s.SubmittedAt)
            .Select(s => s.PayloadJson)
            .FirstOrDefaultAsync(ct);

        return payloadJson is null ? null : ExtractText(payloadJson);
    }

    private async Task<List<PredictionRow>> GetPredictionRowsAsync(Guid sessionId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var raw = await db.PlayerSubmissions
            .Where(s => s.SessionId == sessionId)
            .OrderBy(s => s.SubmittedAt)
            .Select(s => new { s.PayloadJson, PlayerName = s.Player.DisplayName })
            .ToListAsync(ct);

        return raw
            .Select(r => new { r.PlayerName, Text = ExtractText(r.PayloadJson) })
            .Where(r => !string.IsNullOrEmpty(r.Text))
            .Select(r => new PredictionRow(r.PlayerName, r.Text!))
            .ToList();
    }

    private static string? ExtractText(string payloadJson)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson, EventJsonOptions.Default);
            return payload.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private record PredictionRow(string PlayerName, string Text);

    private class PredictionsConfig
    {
        public int Points { get; set; } = 3;
        public string? Prompt { get; set; }
    }
}