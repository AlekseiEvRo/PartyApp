using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Events.Handlers;

/// <summary>
/// Обработчик ивента "emoji_song" (угадай песню по эмодзи).
/// Игрок вводит название текстом; ответ сравнивается с нормализацией
/// (регистр, ё/е, пунктуация). На каждую песню — одна попытка.
/// </summary>
public class EmojiSongHandler : IEventHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPointsAwardService _pointsAward;
    private readonly ILogger<EmojiSongHandler> _logger;

    public EmojiSongHandler(
        IServiceScopeFactory scopeFactory,
        IPointsAwardService pointsAward,
        ILogger<EmojiSongHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _pointsAward = pointsAward;
        _logger = logger;
    }

    public string EventType => "emoji_song";

    public string DefaultConfigJson => """
        {
            "pointsPerCorrect": 5,
            "songs": [
                { "emoji": "🌞🌻", "answer": "Солнечный круг", "hint": "Детская песня про небо" },
                { "emoji": "🎄🌲❄️", "answer": "В лесу родилась ёлочка", "hint": "Новогодняя классика" },
                { "emoji": "🐻🍯🌳", "answer": "Винни-Пух", "hint": "Песенка плюшевого медведя" },
                { "emoji": "🤖🚀", "answer": "Трава у дома", "hint": "Земля в иллюминаторе" },
                { "emoji": "🍦🚶‍♀️", "answer": "Как здорово, что все мы здесь сегодня собрались", "hint": "Песня у костра" },
                { "emoji": "🎉🥳🎂", "answer": "С днём рождения", "hint": "И без этой песни никак" },
                { "emoji": "❄️😊🛷", "answer": "Три белых коня", "hint": "Зимняя песня из «Чародеев»" },
                { "emoji": "🌊🏄‍♂️", "answer": "Комарово", "hint": "На недельку, до второго" }
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
        var config = JsonSerializer.Deserialize<EmojiSongConfig>(definition.ConfigJson, EventJsonOptions.Default);
        var songs = config?.Songs ?? new List<EmojiSong>();
        int points = config?.PointsPerCorrect > 0 ? config.PointsPerCorrect : 5;

        if (songs.Count == 0)
            return SubmissionResult.Fail("Песни не настроены");

        int songIndex = -1;
        string answer = string.Empty;

        if (!string.IsNullOrEmpty(payloadJson))
        {
            try
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson, EventJsonOptions.Default);
                if (payload.TryGetProperty("songIndex", out var indexProp))
                    songIndex = indexProp.GetInt32();
                if (payload.TryGetProperty("answer", out var answerProp))
                    answer = answerProp.GetString()?.Trim() ?? string.Empty;
            }
            catch { /* ignore */ }
        }

        if (songIndex < 0 || songIndex >= songs.Count)
            return SubmissionResult.Fail("Неверный номер песни");

        if (answer.Length == 0)
            return SubmissionResult.Fail("Введи название песни");

        if (await HasAnsweredAsync(session.Id, playerId, songIndex, ct))
            return SubmissionResult.Fail("Ты уже отвечал на эту песню");

        EmojiSong song = songs[songIndex];

        if (Normalize(answer) == Normalize(song.Answer))
        {
            await _pointsAward.AwardAsync(
                playerId,
                points,
                $"Песня: {song.Answer}",
                sessionId: session.Id,
                ct: ct);

            _logger.LogInformation(
                "EmojiSong: player {PlayerId} guessed song {SongIndex} in session {SessionId}",
                playerId, songIndex, session.Id);

            return SubmissionResult.Ok(
                points,
                $"🎵 Верно! Это «{song.Answer}»",
                new { songIndex, correct = true, answer = song.Answer });
        }

        return SubmissionResult.Fail(
            $"Не угадал! Это «{song.Answer}»",
            new { songIndex, correct = false, answer = song.Answer });
    }

    public async Task<object?> GetLiveDataAsync(EventSession session, EventDefinition definition, CancellationToken ct = default)
    {
        var config = JsonSerializer.Deserialize<EmojiSongConfig>(definition.ConfigJson, EventJsonOptions.Default);
        var songs = config?.Songs ?? new List<EmojiSong>();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        int guessedCount = await db.PlayerSubmissions
            .CountAsync(s => s.SessionId == session.Id && s.Score > 0, ct);

        int playersCount = await db.PlayerSubmissions
            .Where(s => s.SessionId == session.Id)
            .Select(s => s.PlayerId)
            .Distinct()
            .CountAsync(ct);

        return new
        {
            songs = songs.Select(s => new { emoji = s.Emoji, hint = s.Hint }).ToArray(),
            guessedCount,
            playersCount
        };
    }

    public async Task<object?> GetPlayerDataAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        List<string> payloads = await db.PlayerSubmissions
            .Where(s => s.SessionId == session.Id && s.PlayerId == playerId)
            .Select(s => s.PayloadJson)
            .ToListAsync(ct);

        var answered = new HashSet<int>();

        foreach (string payloadJson in payloads)
        {
            try
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson, EventJsonOptions.Default);
                if (payload.TryGetProperty("songIndex", out var indexProp) && indexProp.TryGetInt32(out int index))
                    answered.Add(index);
            }
            catch { /* ignore */ }
        }

        return new { answered = answered.OrderBy(i => i).ToArray() };
    }

    private async Task<bool> HasAnsweredAsync(Guid sessionId, Guid playerId, int songIndex, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        List<string> payloads = await db.PlayerSubmissions
            .Where(s => s.SessionId == sessionId && s.PlayerId == playerId)
            .Select(s => s.PayloadJson)
            .ToListAsync(ct);

        foreach (string payloadJson in payloads)
        {
            try
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson, EventJsonOptions.Default);
                if (payload.TryGetProperty("songIndex", out var indexProp)
                    && indexProp.TryGetInt32(out int index)
                    && index == songIndex)
                {
                    return true;
                }
            }
            catch { /* ignore */ }
        }

        return false;
    }

    /// <summary>Нижний регистр, ё→е, без пунктуации и лишних пробелов.</summary>
    private static string Normalize(string value)
    {
        string lower = value.ToLowerInvariant().Replace('ё', 'е');
        char[] chars = lower.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray();
        string[] words = new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return string.Join(' ', words);
    }

    private class EmojiSongConfig
    {
        public int PointsPerCorrect { get; set; } = 5;
        public List<EmojiSong> Songs { get; set; } = new();
    }

    private class EmojiSong
    {
        public string Emoji { get; set; } = string.Empty;
        public string Answer { get; set; } = string.Empty;
        public string? Hint { get; set; }
    }
}