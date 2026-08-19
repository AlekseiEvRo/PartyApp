using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PartyApp.Api.Hubs;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Toast;

public class ToastService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<PartyHub> _hubContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ToastService> _logger;

    private readonly ToastState _state = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ToastService(
        IServiceScopeFactory scopeFactory,
        IHubContext<PartyHub> hubContext,
        IConfiguration configuration,
        ILogger<ToastService> logger)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
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
                await AwardPointsAsync(userId, points, "Тост за именинника", ct);
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

    private async Task AwardPointsAsync(Guid userId, int points, string description, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var wallet = await db.Wallets.SingleOrDefaultAsync(w => w.UserId == userId, ct);

        if (wallet is null)
        {
            wallet = new Domain.Entities.Wallet
            {
                UserId = userId,
                Balance = points
            };
            db.Wallets.Add(wallet);
            await db.SaveChangesAsync(ct); // Сначала сохраняем кошелёк
        }
        else
        {
            wallet.Balance += points;
            await db.SaveChangesAsync(ct); // Обновляем баланс
        }

        // Теперь добавляем транзакцию
        var transaction = new WalletTransaction
        {
            WalletId = wallet.Id, // Теперь Id точно есть
            Amount = points,
            Type = WalletTransactionType.EventReward,
            Description = description
        };

        db.WalletTransactions.Add(transaction);
        await db.SaveChangesAsync(ct);
    }
}