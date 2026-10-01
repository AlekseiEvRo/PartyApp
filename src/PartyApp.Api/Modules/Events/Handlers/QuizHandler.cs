using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Events.Handlers;

/// <summary>
/// Обработчик ивента "quiz".
/// Вопросы с вариантами ответов. Игрок выбирает один вариант.
/// Баллы за правильные ответы.
/// </summary>
public class QuizHandler : IEventHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PointsAwardService _pointsAward;
    private readonly ILogger<QuizHandler> _logger;

    public QuizHandler(
        IServiceScopeFactory scopeFactory,
        PointsAwardService pointsAward,
        ILogger<QuizHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _pointsAward = pointsAward;
        _logger = logger;
    }

    public string EventType => "quiz";

    public async Task<SubmissionResult> HandleSubmissionAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        string payloadJson,
        CancellationToken ct = default)
    {
        // Читаем конфиг
        var config = JsonSerializer.Deserialize<QuizConfig>(definition.ConfigJson, EventJsonOptions.Default);
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

    private async Task<bool> HasPlayerAnsweredQuestionAsync(Guid sessionId, Guid playerId, int questionIndex, CancellationToken ct)
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
                var storedQuestionIndex = payload.TryGetProperty("questionIndex", out var qi) ? qi.GetInt32() : -1;

                if (storedQuestionIndex == questionIndex)
                    return true;
            }
            catch { /* ignore */ }
        }

        return false;
    }

    private class QuizConfig
    {
        public int TimeLimitSec { get; set; } = 20;
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