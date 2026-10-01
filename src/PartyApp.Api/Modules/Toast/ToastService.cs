using Microsoft.AspNetCore.SignalR;
using PartyApp.Api.Hubs;
using PartyApp.Api.Modules.Push;
using PartyApp.Api.Modules.Wallet;

namespace PartyApp.Api.Modules.Toast;

public class ToastService
{
    private readonly IHubContext<PartyHub> _hubContext;
    private readonly PointsAwardService _pointsAward;
    private readonly IPushNotificationService _push;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ToastService> _logger;

    private readonly ToastState _state = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ToastService(
        IHubContext<PartyHub> hubContext,
        PointsAwardService pointsAward,
        IPushNotificationService push,
        IConfiguration configuration,
        ILogger<ToastService> logger)
    {
        _hubContext = hubContext;
        _pointsAward = pointsAward;
        _push = push;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ToastResult> TrySayToastAsync(Guid userId, string username, CancellationToken ct = default)
    {
        var cooldownSeconds = int.Parse(_configuration["Toast:CooldownSeconds"] ?? "60");
        var points = int.Parse(_configuration["Toast:Points"] ?? "1");

        DateTime busyUntilUtc;

        await _lock.WaitAsync(ct);
        try
        {
            var nowUtc = DateTime.UtcNow;

            if (_state.IsBusy(nowUtc))
            {
                return new ToastResult(
                    Success: false,
                    BusyUntilUtc: _state.BusyUntilUtc,
                    BusyByName: _state.CurrentSpeakerName,
                    Points: 0,
                    Message: $"Тост уже говорит {_state.CurrentSpeakerName}");
            }

            busyUntilUtc = nowUtc.AddSeconds(cooldownSeconds);

            _state.CurrentSpeakerId = userId;
            _state.CurrentSpeakerName = username;
            _state.BusyUntilUtc = busyUntilUtc;

            try
            {
                await _pointsAward.AwardAsync(userId, points, "Тост за именинника", ct: ct);
            }
            catch
            {
                // Если не удалось начислить баллы, сбрасываем блокировку
                _state.CurrentSpeakerId = null;
                _state.CurrentSpeakerName = null;
                _state.BusyUntilUtc = null;
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }

        // Рассылаем всем уведомление
        await _hubContext.Clients.All.SendAsync("ToastStarted", new
        {
            userId,
            username,
            busyUntilUtc
        }, ct);

        // Push всем, кроме самого говорящего
        await _push.SendToAllAsync(
            new PushMessage(
                Title: "🍾 Тосты!",
                Body: $"{username} говорит тост — скорее слушай!",
                Url: "/",
                Tag: "toast"),
            excludedUserId: userId,
            ct);

        _logger.LogInformation("User {Username} said a toast. Cooldown until {UntilUtc}", username, busyUntilUtc);

        return new ToastResult(
            Success: true,
            BusyUntilUtc: busyUntilUtc,
            BusyByName: null,
            Points: points,
            Message: "Тост засчитан!");
    }

    public ToastStatus GetStatus()
    {
        var nowUtc = DateTime.UtcNow;

        if (_state.IsBusy(nowUtc))
        {
            return new ToastStatus(true, _state.BusyUntilUtc, _state.CurrentSpeakerName);
        }

        return new ToastStatus(false, null, null);
    }
}