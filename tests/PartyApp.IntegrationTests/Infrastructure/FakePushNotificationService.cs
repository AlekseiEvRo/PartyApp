using PartyApp.Api.Modules.Push;

namespace PartyApp.IntegrationTests.Infrastructure;

/// <summary>
/// Подменяет реальную отправку push и записывает вызовы.
/// </summary>
public sealed class FakePushNotificationService : IPushNotificationService
{
    private readonly object _lock = new();
    private readonly List<PushCall> _calls = new();

    public IReadOnlyList<PushCall> Calls
    {
        get
        {
            lock (_lock)
            {
                return _calls.ToList();
            }
        }
    }

    public string? PublicKey { get; set; } = "test-vapid-public-key";

    public Task SendToAllAsync(PushMessage message, Guid? excludedUserId = null, CancellationToken ct = default)
    {
        Record(new PushCall(message, null, excludedUserId));
        return Task.CompletedTask;
    }

    public Task SendToUserAsync(Guid userId, PushMessage message, CancellationToken ct = default)
    {
        Record(new PushCall(message, new[] { userId }, null));
        return Task.CompletedTask;
    }

    public Task SendToUsersAsync(IReadOnlyCollection<Guid> userIds, PushMessage message, CancellationToken ct = default)
    {
        Record(new PushCall(message, userIds.ToArray(), null));
        return Task.CompletedTask;
    }

    public Task<PushDeliveryReport> SendToUserNowAsync(Guid userId, PushMessage message, CancellationToken ct = default)
    {
        Record(new PushCall(message, new[] { userId }, null));
        return Task.FromResult(new PushDeliveryReport(2, 0, 0));
    }

    public void Clear()
    {
        lock (_lock)
        {
            _calls.Clear();
        }
    }

    private void Record(PushCall call)
    {
        lock (_lock)
        {
            _calls.Add(call);
        }
    }
}

public sealed record PushCall(PushMessage Message, Guid[]? UserIds, Guid? ExcludedUserId);