using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PartyApp.Api.Hubs;
using PartyApp.Api.Modules.Push;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Moderation;

/// <summary>
/// Уведомляет администраторов о новом контенте на модерации:
/// живое событие по SignalR в группу «admins» и push, если приложение закрыто.
/// </summary>
public class ModerationNotifier
{
    private static readonly TimeSpan PushThrottle = TimeSpan.FromSeconds(30);

    private readonly AppDbContext _db;
    private readonly IHubContext<PartyHub> _hub;
    private readonly IPushNotificationService _push;
    private readonly ILogger<ModerationNotifier> _logger;

    public ModerationNotifier(
        AppDbContext db,
        IHubContext<PartyHub> hub,
        IPushNotificationService push,
        ILogger<ModerationNotifier> logger)
    {
        _db = db;
        _hub = hub;
        _push = push;
        _logger = logger;
    }

    /// <param name="kind">"photo" или "wish"</param>
    /// <param name="itemId">ID элемента, который ждёт проверки</param>
    /// <param name="authorName">Имя автора, чтобы админ понимал, от кого контент</param>
    /// <param name="preview">Короткий текст отзыва; для фото null</param>
    public async Task NotifyPendingAsync(
        string kind,
        Guid itemId,
        string authorName,
        string? preview,
        CancellationToken ct = default)
    {
        await _hub.Clients.Group(PartyHub.AdminsGroup).SendAsync("ModerationPending", new
        {
            kind,
            itemId,
            authorName,
            preview
        }, ct);

        List<Guid> adminIds = await _db.Users
            .Where(u => u.Role == UserRole.Admin || u.Role == UserRole.SuperAdmin)
            .Select(u => u.Id)
            .ToListAsync(ct);

        if (adminIds.Count == 0)
            return;

        string title = kind == "photo"
            ? "📸 Новое фото на модерации"
            : "💌 Новый отзыв на модерации";

        string body = string.IsNullOrWhiteSpace(preview)
            ? $"От {authorName}"
            : $"{authorName}: {Truncate(preview, 80)}";

        await _push.SendToUsersAsync(
            adminIds,
            new PushMessage(
                Title: title,
                Body: body,
                Url: "/admin",
                // Теги раздельные: уведомление об отзыве не заменяет фото (и наоборот)
                Tag: $"moderation-{kind}",
                ThrottleWindow: PushThrottle),
            ct);

        _logger.LogInformation(
            "Moderation pending: kind={Kind}, item={ItemId}, author={Author}",
            kind, itemId, authorName);
    }

    private static string Truncate(string text, int maxLength)
    {
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }
}