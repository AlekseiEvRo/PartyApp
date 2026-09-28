using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using PartyApp.Infrastructure.Persistence;

using WebPush;

using PushSubscriptionEntity = PartyApp.Domain.Entities.PushSubscription;
using WebPushSubscription = WebPush.PushSubscription;

namespace PartyApp.Api.Modules.Push;

/// <summary>
/// Отправка Web Push через Apple/Google push-сервисы.
/// Singleton: подписки читаются из БД через отдельный scope.
/// </summary>
public class PushNotificationService : IPushNotificationService
{
    private const int MaxParallelSends = 8;
    private const int EndpointLogLength = 80;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PushNotificationService> _logger;
    private readonly WebPushClient _client = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastSentAt = new(StringComparer.Ordinal);
    private readonly VapidDetails? _vapid;

    public PushNotificationService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<PushNotificationService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _vapid = LoadOrCreateVapidKeys(configuration, logger);
    }

    public string? PublicKey => _vapid?.PublicKey;

    public async Task SendToAllAsync(PushMessage message, Guid? excludedUserId = null, CancellationToken ct = default)
    {
        List<PushSubscriptionEntity> recipients = await LoadSubscriptionsAsync(userIds: null, excludedUserId, ct);
        QueueInBackground(recipients, message);
    }

    public async Task SendToUserAsync(Guid userId, PushMessage message, CancellationToken ct = default)
    {
        List<PushSubscriptionEntity> recipients = await LoadSubscriptionsAsync([userId], excludedUserId: null, ct);
        QueueInBackground(recipients, message);
    }

    public async Task SendToUsersAsync(IReadOnlyCollection<Guid> userIds, PushMessage message, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return;

        List<PushSubscriptionEntity> recipients = await LoadSubscriptionsAsync(userIds, excludedUserId: null, ct);
        QueueInBackground(recipients, message);
    }

    public async Task<PushDeliveryReport> SendToUserNowAsync(Guid userId, PushMessage message, CancellationToken ct = default)
    {
        List<PushSubscriptionEntity> recipients = await LoadSubscriptionsAsync([userId], excludedUserId: null, ct);
        List<PushSubscriptionEntity> allowed = ApplyThrottle(recipients, message);

        return await DeliverAsync(allowed, message, ct);
    }

    private void QueueInBackground(List<PushSubscriptionEntity> recipients, PushMessage message)
    {
        if (_vapid is null)
        {
            _logger.LogWarning("Push «{Title}» не отправлен: VAPID-ключи не настроены", message.Title);
            return;
        }

        List<PushSubscriptionEntity> allowed = ApplyThrottle(recipients, message);
        if (allowed.Count == 0)
            return;

        // Отправку не ждём: админ не должен ждать, пока push уйдёт на все телефоны
        _ = Task.Run(() => DeliverAsync(allowed, message, CancellationToken.None), CancellationToken.None);
    }

    /// <summary>
    /// Отбрасывает получателей, которым недавно уже отправляли сообщение с этим throttle-окном.
    /// </summary>
    private List<PushSubscriptionEntity> ApplyThrottle(List<PushSubscriptionEntity> recipients, PushMessage message)
    {
        if (message.ThrottleWindow is not TimeSpan window)
            return recipients;

        DateTime now = DateTime.UtcNow;
        List<PushSubscriptionEntity> allowed = new(recipients.Count);

        foreach (PushSubscriptionEntity recipient in recipients)
        {
            string key = recipient.UserId.ToString();
            if (_lastSentAt.TryGetValue(key, out DateTime lastSentAt) && now - lastSentAt < window)
                continue;

            _lastSentAt[key] = now;
            allowed.Add(recipient);
        }

        return allowed;
    }

    private async Task<PushDeliveryReport> DeliverAsync(
        IReadOnlyList<PushSubscriptionEntity> subscriptions,
        PushMessage message,
        CancellationToken ct)
    {
        if (_vapid is null || subscriptions.Count == 0)
            return new PushDeliveryReport(0, 0, 0);

        string payload = JsonSerializer.Serialize(new
        {
            title = message.Title,
            body = message.Body,
            url = message.Url ?? "/",
            tag = message.Tag
        });

        int sent = 0;
        int failed = 0;
        ConcurrentBag<PushSubscriptionEntity> stale = new();

        await Parallel.ForEachAsync(
            subscriptions,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelSends, CancellationToken = ct },
            async (subscription, token) =>
            {
                try
                {
                    WebPushSubscription target = new(subscription.Endpoint, subscription.P256dh, subscription.Auth);
                    await _client.SendNotificationAsync(target, payload, _vapid, token);
                    Interlocked.Increment(ref sent);
                }
                catch (WebPushException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                {
                    stale.Add(subscription);
                    _logger.LogInformation(
                        "Push-подписка устарела и будет удалена: {Endpoint}",
                        Shorten(subscription.Endpoint));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    _logger.LogWarning(
                        ex,
                        "Не удалось отправить push «{Title}» на {Endpoint}",
                        message.Title,
                        Shorten(subscription.Endpoint));
                }
            });

        if (!stale.IsEmpty)
            await RemoveStaleAsync(stale.ToList());

        return new PushDeliveryReport(sent, failed, stale.Count);
    }

    private async Task<List<PushSubscriptionEntity>> LoadSubscriptionsAsync(
        IReadOnlyCollection<Guid>? userIds,
        Guid? excludedUserId,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        IQueryable<PushSubscriptionEntity> query = db.PushSubscriptions.AsNoTracking();

        if (userIds is not null)
            query = query.Where(s => userIds.Contains(s.UserId));

        if (excludedUserId.HasValue)
            query = query.Where(s => s.UserId != excludedUserId.Value);

        return await query.ToListAsync(ct);
    }

    private async Task RemoveStaleAsync(IReadOnlyCollection<PushSubscriptionEntity> staleSubscriptions)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            List<string> endpoints = staleSubscriptions.Select(s => s.Endpoint).ToList();
            List<PushSubscriptionEntity> items = await db.PushSubscriptions
                .Where(s => endpoints.Contains(s.Endpoint))
                .ToListAsync();

            db.PushSubscriptions.RemoveRange(items);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось удалить устаревшие push-подписки");
        }
    }

    private static string Shorten(string endpoint)
    {
        return endpoint.Length <= EndpointLogLength ? endpoint : endpoint[..EndpointLogLength] + "…";
    }

    /// <summary>
    /// Берёт VAPID-ключи из конфига, из App_Data/vapid.json или генерирует новые.
    /// </summary>
    private static VapidDetails? LoadOrCreateVapidKeys(IConfiguration configuration, ILogger logger)
    {
        string subject = configuration["Push:Subject"] ?? "mailto:admin@party-app.online";
        string? publicKey = configuration["Push:PublicKey"];
        string? privateKey = configuration["Push:PrivateKey"];

        if (!string.IsNullOrWhiteSpace(publicKey) && !string.IsNullOrWhiteSpace(privateKey))
        {
            try
            {
                return new VapidDetails(subject, publicKey, privateKey);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "VAPID-ключи из конфига некорректны, push-уведомления отключены");
                return null;
            }
        }

        string path = configuration["Push:KeysFile"] ?? "App_Data/vapid.json";

        try
        {
            if (File.Exists(path))
            {
                StoredVapidKeys? stored = JsonSerializer.Deserialize<StoredVapidKeys>(File.ReadAllText(path));
                if (stored is not null
                    && !string.IsNullOrWhiteSpace(stored.PublicKey)
                    && !string.IsNullOrWhiteSpace(stored.PrivateKey))
                {
                    return new VapidDetails(stored.Subject ?? subject, stored.PublicKey, stored.PrivateKey);
                }
            }

            VapidDetails generated = VapidHelper.GenerateVapidKeys();
            StoredVapidKeys keys = new()
            {
                Subject = subject,
                PublicKey = generated.PublicKey,
                PrivateKey = generated.PrivateKey
            };

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonSerializer.Serialize(keys, new JsonSerializerOptions { WriteIndented = true }));
            logger.LogInformation("VAPID-ключи сгенерированы и сохранены в {Path}", path);

            return new VapidDetails(subject, keys.PublicKey, keys.PrivateKey);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Не удалось подготовить VAPID-ключи, push-уведомления отключены");
            return null;
        }
    }

    private class StoredVapidKeys
    {
        public string? Subject { get; set; }
        public string PublicKey { get; set; } = string.Empty;
        public string PrivateKey { get; set; } = string.Empty;
    }
}