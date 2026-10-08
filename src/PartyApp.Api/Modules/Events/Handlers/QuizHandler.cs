using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PartyApp.Api.Hubs;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Events.Handlers;

/// <summary>
/// Обработчик ивента "quiz".
/// Вопросы с вариантами ответов. Игрок выбирает один вариант.
/// Баллы за правильные ответы.
/// Большой экран показывает только общий счётчик ответивших,
/// сами вопросы остаются на телефонах игроков.
/// </summary>
public class QuizHandler : IEventHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPointsAwardService _pointsAward;
    private readonly IHubContext<PartyHub> _hub;
    private readonly ILogger<QuizHandler> _logger;

    public QuizHandler(
        IServiceScopeFactory scopeFactory,
        IPointsAwardService pointsAward,
        IHubContext<PartyHub> hub,
        ILogger<QuizHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _pointsAward = pointsAward;
        _hub = hub;
        _logger = logger;
    }

    public string EventType => "quiz";

    public string DefaultConfigJson => """
        {
            "pointsPerCorrect": 10,
            "questions": [
                {
                    "text": "Вопрос про именинника?",
                    "options": ["Вариант 1", "Вариант 2", "Вариант 3", "Вариант 4"],
                    "correctIndex": 0
                }
            ]
        }
        """;

    /// <summary>
    /// Отдаём только тексты вопросов и варианты. Без correctIndex (правильный ответ
    /// не должен утекать игрокам) и без timeLimitSec (сервер это время не проверяет,
    /// а экран показывал по нему фантомный таймер; реальное время — из endsAt сессии).
    /// </summary>
    public object? GetPublicConfig(EventDefinition definition)
    {
        QuizConfig? config = ParseConfig(definition);
        if (config is null)
            return new { };

        return new
        {
            pointsPerCorrect = config.PointsPerCorrect > 0 ? config.PointsPerCorrect : 10,
            questions = (config.Questions ?? new List<QuizQuestion>()).Select(q => new
            {
                text = q.Text,
                options = q.Options
            }).ToList()
        };
    }

    public async Task<SubmissionResult> HandleSubmissionAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        string payloadJson,
        CancellationToken ct = default)
    {
        // Читаем конфиг
        QuizConfig? config = ParseConfig(definition);
        if (config is null || config.Questions is null || config.Questions.Count == 0)
            return SubmissionResult.Fail("Квиз не настроен");

        var pointsPerCorrect = config.PointsPerCorrect > 0 ? config.PointsPerCorrect : 10;

        // Извлекаем ответ из payload
        int questionIndex = -1;
        int answerIndex = -1;

        if (!string.IsNullOrEmpty(payloadJson))
        {
            try
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson, EventJsonOptions.Default);
                questionIndex = payload.TryGetProperty("questionIndex", out var qi) ? qi.GetInt32() : -1;
                answerIndex = payload.TryGetProperty("answerIndex", out var ai) ? ai.GetInt32() : -1;
            }
            catch { /* ignore */ }
        }

        if (questionIndex < 0 || questionIndex >= config.Questions.Count)
            return SubmissionResult.Fail("Неверный индекс вопроса");

        var question = config.Questions[questionIndex];

        if (answerIndex < 0 || answerIndex >= question.Options.Count)
            return SubmissionResult.Fail("Неверный индекс ответа");

        // Проверяем, не отвечал ли игрок уже на этот вопрос
        var alreadyAnswered = await HasPlayerAnsweredQuestionAsync(session.Id, playerId, questionIndex, ct);
        if (alreadyAnswered)
            return SubmissionResult.Fail("Ты уже ответил на этот вопрос");

        // Проверяем правильность
        var isCorrect = answerIndex == question.CorrectIndex;
        var points = isCorrect ? pointsPerCorrect : 0;

        // Начисляем баллы только за правильный ответ
        if (isCorrect)
        {
            await _pointsAward.AwardAsync(
                playerId,
                points,
                $"Квиз: вопрос {questionIndex + 1}",
                sessionId: session.Id,
                ct: ct);
        }

        _logger.LogInformation(
            "Player {PlayerId} answered question {QuestionIndex} in session {SessionId}. Correct: {IsCorrect}",
            playerId, questionIndex, session.Id, isCorrect);

        return SubmissionResult.Ok(
            points,
            isCorrect ? "Правильно! 🎉" : "Неправильно 😔",
            new
            {
                questionIndex,
                answerIndex,
                isCorrect,
                correctIndex = question.CorrectIndex,
                points
            });
    }

    /// <summary>
    /// Сводка для большого экрана: сколько игроков ответило на все вопросы
    /// и сколько игроков всего участвуют. Повторные и отклонённые попытки
    /// не удваиваются — считаем игроков, а не строки сабмитов.
    /// </summary>
    public async Task<object?> GetLiveDataAsync(
        EventSession session,
        EventDefinition definition,
        CancellationToken ct = default)
    {
        QuizConfig? config = ParseConfig(definition);
        int questionCount = config?.Questions?.Count ?? 0;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var submissions = await db.PlayerSubmissions.AsNoTracking()
            .Where(s => s.SessionId == session.Id)
            .Select(s => new { s.PlayerId, s.PayloadJson })
            .ToListAsync(ct);

        Dictionary<Guid, HashSet<int>> answeredByPlayer = new();

        foreach (var submission in submissions)
        {
            (int QuestionIndex, int AnswerIndex) answer = ReadAnswer(submission.PayloadJson);
            if (answer.QuestionIndex < 0 || answer.QuestionIndex >= questionCount)
                continue;

            if (!answeredByPlayer.TryGetValue(submission.PlayerId, out HashSet<int>? answered))
            {
                answered = new HashSet<int>();
                answeredByPlayer[submission.PlayerId] = answered;
            }

            answered.Add(answer.QuestionIndex);
        }

        // Ответили на все вопросы (если вопросов нет — считать нечего)
        int answeredAll = questionCount > 0
            ? answeredByPlayer.Values.Count(answered => answered.Count == questionCount)
            : 0;

        // Сколько всего участников: незабаненные игроки, без админов
        int totalPlayers = await db.Users
            .CountAsync(u => u.Role == UserRole.Player && u.IsActive, ct);

        return new
        {
            answeredAll,
            players = answeredByPlayer.Count,
            totalPlayers
        };
    }

    /// <summary>
    /// Ответы игрока: карточка подсвечивает выбранные варианты (зелёный/красный)
    /// и восстанавливает их после перезагрузки страницы.
    /// </summary>
    public async Task<object?> GetPlayerDataAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        CancellationToken ct = default)
    {
        QuizConfig? config = ParseConfig(definition);
        if (config?.Questions is null || config.Questions.Count == 0)
            return new { answers = Array.Empty<object>() };

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var payloads = await db.PlayerSubmissions.AsNoTracking()
            .Where(s => s.SessionId == session.Id && s.PlayerId == playerId)
            .Select(s => s.PayloadJson)
            .ToListAsync(ct);

        List<object> answers = new();
        HashSet<int> seenQuestions = new();

        foreach (string payloadJson in payloads)
        {
            (int QuestionIndex, int AnswerIndex) answer = ReadAnswer(payloadJson);
            if (answer.QuestionIndex < 0 || answer.QuestionIndex >= config.Questions.Count)
                continue;

            // Повторные попытки по тому же вопросу не дублируем
            if (!seenQuestions.Add(answer.QuestionIndex))
                continue;

            QuizQuestion question = config.Questions[answer.QuestionIndex];
            if (answer.AnswerIndex < 0 || answer.AnswerIndex >= question.Options.Count)
                continue;

            answers.Add(new
            {
                questionIndex = answer.QuestionIndex,
                answerIndex = answer.AnswerIndex,
                isCorrect = answer.AnswerIndex == question.CorrectIndex,
                correctIndex = question.CorrectIndex
            });
        }

        return new { answers };
    }

    /// <summary>
    /// После ответа игрока обновляем счётчики на большом экране.
    /// Сабмит уже сохранён в БД к этому моменту (вызывается из EventService).
    /// </summary>
    public async Task AfterSubmissionAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        CancellationToken ct = default)
    {
        object? live = await GetLiveDataAsync(session, definition, ct);
        await _hub.Clients.All.SendAsync("EventLiveUpdated", new { sessionId = session.Id, live }, ct);
    }

    private async Task<bool> HasPlayerAnsweredQuestionAsync(Guid sessionId, Guid playerId, int questionIndex, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var playerSubmissions = await db.PlayerSubmissions
            .Where(s => s.SessionId == sessionId && s.PlayerId == playerId)
            .ToListAsync(ct);

        foreach (var submission in playerSubmissions)
        {
            if (ReadAnswer(submission.PayloadJson).QuestionIndex == questionIndex)
                return true;
        }

        return false;
    }

    private static QuizConfig? ParseConfig(EventDefinition definition)
    {
        try
        {
            return JsonSerializer.Deserialize<QuizConfig>(definition.ConfigJson, EventJsonOptions.Default);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Индексы вопроса и ответа из payload сабмита; -1 — поля нет или payload битый.
    /// </summary>
    private static (int QuestionIndex, int AnswerIndex) ReadAnswer(string payloadJson)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson, EventJsonOptions.Default);
            int questionIndex = payload.TryGetProperty("questionIndex", out var qi) ? qi.GetInt32() : -1;
            int answerIndex = payload.TryGetProperty("answerIndex", out var ai) ? ai.GetInt32() : -1;
            return (questionIndex, answerIndex);
        }
        catch
        {
            return (-1, -1);
        }
    }

    private class QuizConfig
    {
        public int PointsPerCorrect { get; set; } = 10;
        public List<QuizQuestion> Questions { get; set; } = new();
    }

    private class QuizQuestion
    {
        public string Text { get; set; } = string.Empty;
        public List<string> Options { get; set; } = new();
        public int CorrectIndex { get; set; }
    }
}