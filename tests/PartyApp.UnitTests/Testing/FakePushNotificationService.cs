using PartyApp.Api.Modules.Push;

namespace PartyApp.UnitTests.Testing;

/// <summary>
/// Подменяет реальную отправку push: записывает вызовы, чтобы тесты могли их проверить.
/// </summary>
public sealed class FakePushNotificationService : IPushNotificationService
{
    public List<PushCall> Calls { get; } = new();

    public string? PublicKey { get; set; } = "test-public-key";

    public PushDeliveryReport NowReport { get; set; } = new(1, 0, 0);

    public Task SendToAllAsync(PushMessage message, Guid? excludedUserId = null, CancellationToken ct = default)
    {
        Calls.Add(new PushCall(message, null, excludedUserId));
        return Task.CompletedTask;
    }

    public Task SendToUserAsync(Guid userId, PushMessage message, CancellationToken ct = default)
    {
        Calls.Add(new PushCall(message, new[] { userId }, null));
        return Task.CompletedTask;
    }

    public Task SendToUsersAsync(IReadOnlyCollection<Guid> userIds, PushMessage message, CancellationToken ct = default)
    {
        Calls.Add(new PushCall(message, userIds.ToArray(), null));
        return Task.CompletedTask;
    }

    public Task<PushDeliveryReport> SendToUserNowAsync(Guid userId, PushMessage message, CancellationToken ct = default)
    {
        Calls.Add(new PushCall(message, new[] { userId }, null));
        return Task.FromResult(NowReport);
    }
}

public sealed record PushCall(PushMessage Message, Guid[]? UserIds, Guid? ExcludedUserId);