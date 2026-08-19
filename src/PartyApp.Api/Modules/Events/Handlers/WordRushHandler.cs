using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PartyApp.Api.Modules.Events.Services;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Events.Handlers;

/// <summary>
/// Обработчик ивента "word_rush".
/// Игрок вводит слова, содержащие буквы А и Е.
/// Проверяется существование слова в русском языке.
/// </summary>
public class WordRushHandler : IEventHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RussianDictionaryService _dictionary;
    private readonly ILogger<WordRushHandler> _logger;

    public WordRushHandler(
        IServiceScopeFactory scopeFactory,
        RussianDictionaryService dictionary,
        ILogger<WordRushHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _dictionary = dictionary;
        _logger = logger;
    }

    public string EventType => "word_rush";

    public async Task<SubmissionResult> HandleSubmissionAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        string payloadJson,
        CancellationToken ct = default)
    {
        // Читаем конфиг
        var config = JsonSerializer.Deserialize<WordRushConfig>(definition.ConfigJson, EventJsonOptions.Default);
        if (config is null)
            return SubmissionResult.Fail("Игра не настроена");

        var requiredLetters = config.RequiredLetters ?? new List<string> { "А", "Е" };
        var minWordLength = config.MinWordLength > 0 ? config.MinWordLength : 3;
        var pointsPerWord = config.PointsPerWord > 0 ? config.PointsPerWord : 5;
        var uniqueWordsOnly = config.UniqueWordsOnly;

        // Извлекаем слово из payload
        string? submittedWord = null;
        if (!string.IsNullOrEmpty(payloadJson))
        {
            try
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson, EventJsonOptions.Default);
                submittedWord = payload.TryGetProperty("word", out var wordProp) ? wordProp.GetString()?.Trim() : null;
            }
            catch { /* ignore */ }
        }

        if (string.IsNullOrWhiteSpace(submittedWord))
            return SubmissionResult.Fail("Введи слово");

        // Нормализуем
        var normalizedWord = submittedWord.ToLowerInvariant().Replace('ё', 'е');

        // Проверяем длину
        if (normalizedWord.Length < minWordLength)
            return SubmissionResult.Fail($"Слово должно быть минимум {minWordLength} буквы");

        // Проверяем наличие требуемых букв
        foreach (var letter in requiredLetters)
        {
            var normalizedLetter = letter.ToLowerInvariant().Replace('ё', 'е');
            if (!normalizedWord.Contains(normalizedLetter))
                return SubmissionResult.Fail($"Слово должно содержать букву «{letter}»");
        }

        // Проверяем существование в словаре
        if (!_dictionary.WordExists(normalizedWord))
            return SubmissionResult.Fail($"Слово «{submittedWord}» не найдено в словаре русского языка или словарь не загружен.");

        // Проверяем уникальность
        if (uniqueWordsOnly)
        {
            var alreadyUsed = await HasWordBeenUsedAsync(session.Id, playerId, normalizedWord, ct);
            if (alreadyUsed)
                return SubmissionResult.Fail("Ты уже использовал это слово");
        }

        // Начисляем баллы
        await AwardPointsAsync(playerId, pointsPerWord, session.Id, $"Слово: {submittedWord}", ct);

        _logger.LogInformation(
            "Player {PlayerId} submitted word {Word} in session {SessionId}",
            playerId, submittedWord, session.Id);

        return SubmissionResult.Ok(
            pointsPerWord,
            $"Слово «{submittedWord}» засчитано!",
            new { word = submittedWord, points = pointsPerWord });
    }

    private async Task<bool> HasWordBeenUsedAsync(Guid sessionId, Guid playerId, string word, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var playerSubmissions = await db.PlayerSubmissions
            .Where(s => s.SessionId == sessionId && s.PlayerId == playerId)
            .ToListAsync(ct);

        foreach (var submission in playerSubmissions)
        {
            try
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(submission.PayloadJson, EventJsonOptions.Default);
                var storedWord = payload.TryGetProperty("word", out var wordProp) ? wordProp.GetString() : null;

                if (storedWord is not null &&
                    string.Equals(storedWord.ToLowerInvariant().Replace('ё', 'е'), word, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { /* ignore */ }
        }

        return false;
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

    private class WordRushConfig
    {
        public int TimeLimitSec { get; set; } = 60;
        public List<string> RequiredLetters { get; set; } = new() { "А", "Е" };
        public int MinWordLength { get; set; } = 3;
        public int PointsPerWord { get; set; } = 5;
        public bool UniqueWordsOnly { get; set; } = true;
    }
}