using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

using PartyApp.Api.Hubs;
using PartyApp.Api.Modules.Push;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Achievements;

/// <summary>
/// Выдаёт достижения по событиям приложения. Проверки никогда не ломают основной
/// сценарий: ошибки логируются, но наружу не пробрасываются.
/// </summary>
public class AchievementService
{
    private const int PhotographerPhotoCount = 5;
    private const int PhotoLovedLikes = 5;
    private const int QrHunterCount = 5;
    private const int DareStarCount = 3;
    private const int RichPoints = 100;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPointsAwardService _pointsAward;
    private readonly IHubContext<PartyHub> _hub;
    private readonly IPushNotificationService _push;
    private readonly ILogger<AchievementService> _logger;

    public AchievementService(
        IServiceScopeFactory scopeFactory,
        IPointsAwardService pointsAward,
        IHubContext<PartyHub> hub,
        IPushNotificationService push,
        ILogger<AchievementService> logger)
    {
        _scopeFactory = scopeFactory;
        _pointsAward = pointsAward;
        _hub = hub;
        _push = push;
        _logger = logger;
    }

    /// <summary>Накопительные достижения: первый ответ, фотограф, QR, фанты, сотня баллов.</summary>
    public async Task EvaluatePlayerAsync(Guid userId, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            bool hasSubmissions = await db.PlayerSubmissions.AsNoTracking()
                .AnyAsync(s => s.PlayerId == userId, ct);
            if (hasSubmissions)
                await AwardAsync(userId, AchievementCatalog.Get(AchievementCatalog.FirstAnswer), ct);

            int approvedPhotos = await db.PartyPhotos.AsNoTracking()
                .CountAsync(p => p.UploadedById == userId && p.Status == ModerationStatus.Approved, ct);
            if (approvedPhotos >= PhotographerPhotoCount)
                await AwardAsync(userId, AchievementCatalog.Get(AchievementCatalog.Photographer), ct);

            int redeemedQr = await db.QrTokens.AsNoTracking()
                .CountAsync(t => t.RedeemedById == userId, ct);
            if (redeemedQr >= QrHunterCount)
                await AwardAsync(userId, AchievementCatalog.Get(AchievementCatalog.QrHunter), ct);

            int confirmedDares = await db.DareAssignments.AsNoTracking()
                .CountAsync(a => a.PlayerId == userId && a.Status == DareStatus.Confirmed, ct);
            if (confirmedDares >= DareStarCount)
                await AwardAsync(userId, AchievementCatalog.Get(AchievementCatalog.DareStar), ct);

            // Приветственный бонус и переводы не считаем: только реально заработанное
            int earned = await db.WalletTransactions.AsNoTracking()
                .Where(t => t.Wallet.UserId == userId
                            && t.Amount > 0
                            && (t.Type == WalletTransactionType.EventReward
                                || t.Type == WalletTransactionType.QrBonus
                                || t.Type == WalletTransactionType.PhotoReward
                                || t.Type == WalletTransactionType.Achievement))
                .SumAsync(t => (int?)t.Amount, ct) ?? 0;

            if (earned >= RichPoints)
                await AwardAsync(userId, AchievementCatalog.Get(AchievementCatalog.Rich100), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось проверить достижения игрока {UserId}", userId);
        }
    }

    /// <summary>Победитель квиза определяется после завершения сессии.</summary>
    public async Task OnEventFinishedAsync(Guid sessionId, string eventType, CancellationToken ct = default)
    {
        if (!string.Equals(eventType, "quiz", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var scores = await db.PlayerSubmissions.AsNoTracking()
                .Where(s => s.SessionId == sessionId)
                .GroupBy(s => s.PlayerId)
                .Select(g => new { PlayerId = g.Key, Score = g.Sum(s => s.Score ?? 0) })
                .ToListAsync(ct);

            int best = scores.Count == 0 ? 0 : scores.Max(s => s.Score);
            if (best <= 0)
                return;

            foreach (var winner in scores.Where(s => s.Score == best))
                await AwardAsync(winner.PlayerId, AchievementCatalog.Get(AchievementCatalog.QuizWinner), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось определить победителя квиза в сессии {SessionId}", sessionId);
        }
    }

    /// <summary>Любимое фото: автору за 5 лайков на одном снимке.</summary>
    public async Task OnPhotoLikedAsync(Guid photoId, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var photo = await db.PartyPhotos.AsNoTracking()
                .Where(p => p.Id == photoId)
                .Select(p => new { p.UploadedById, Likes = p.Likes.Count })
                .SingleOrDefaultAsync(ct);

            if (photo is null || photo.Likes < PhotoLovedLikes)
                return;

            await AwardAsync(photo.UploadedById, AchievementCatalog.Get(AchievementCatalog.PhotoLoved), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось проверить достижение «любимое фото» для {PhotoId}", photoId);
        }
    }

    /// <summary>Линия бинго: вызывается сервисом бинго после начисления бонуса.</summary>
    public async Task OnBingoLineAsync(Guid playerId, CancellationToken ct = default)
    {
        try
        {
            await AwardAsync(playerId, AchievementCatalog.Get(AchievementCatalog.BingoLine), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось выдать достижение за линию бинго игроку {PlayerId}", playerId);
        }
    }

    private async Task AwardAsync(Guid userId, AchievementDefinition definition, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        bool alreadyAwarded = await db.UserAchievements.AnyAsync(
            a => a.UserId == userId && a.Code == definition.Code, ct);

        if (alreadyAwarded)
            return;

        try
        {
            db.UserAchievements.Add(new UserAchievement
            {
                UserId = userId,
                Code = definition.Code
            });
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Параллельная проверка уже выдала достижение — ничего страшного
            return;
        }

        await _pointsAward.AwardAsync(
            userId,
            definition.Points,
            $"Достижение: {definition.Title}",
            WalletTransactionType.Achievement,
            ct: ct);

        await _hub.Clients.User(userId.ToString()).SendAsync("AchievementUnlocked", new
        {
            code = definition.Code,
            title = definition.Title,
            icon = definition.Icon,
            description = definition.Description,
            points = definition.Points
        }, ct);

        await _push.SendToUserAsync(
            userId,
            new PushMessage(
                Title: $"🏅 {definition.Icon} {definition.Title}",
                Body: $"Достижение! +{definition.Points} баллов",
                Url: "/",
                Tag: $"achievement-{definition.Code}"),
            ct);

        _logger.LogInformation(
            "Achievement unlocked: user={UserId}, code={Code}", userId, definition.Code);
    }
}
