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
/// Обработчик ивента "qr_scan".
/// Игрок сканирует QR-код или вводит код вручную.
/// Каждый токен одноразовый.
/// </summary>
public class QrScanHandler : IEventHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPointsAwardService _pointsAward;
    private readonly IHubContext<PartyHub> _hub;
    private readonly ILogger<QrScanHandler> _logger;

    public QrScanHandler(
        IServiceScopeFactory scopeFactory,
        IPointsAwardService pointsAward,
        IHubContext<PartyHub> hub,
        ILogger<QrScanHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _pointsAward = pointsAward;
        _hub = hub;
        _logger = logger;
    }

    public string EventType => "qr_scan";

    public string DefaultConfigJson => "{}";

    public async Task<SubmissionResult> HandleSubmissionAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        string payloadJson,
        CancellationToken ct = default)
    {
        // Извлекаем код из payload
        string? submittedCode = null;
        if (!string.IsNullOrEmpty(payloadJson))
        {
            try
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson, EventJsonOptions.Default);
                submittedCode = payload.TryGetProperty("code", out var codeProp)
                    ? codeProp.GetString()?.Trim().ToUpperInvariant()
                    : null;
            }
            catch { /* ignore */ }
        }

        if (string.IsNullOrWhiteSpace(submittedCode))
            return SubmissionResult.Fail("Введи код с QR");

        // Ищем токен в БД
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var token = await db.QrTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Code == submittedCode, ct);

        if (token is null)
            return SubmissionResult.Fail("Код не найден");

        if (token.RedeemedAt.HasValue)
            return SubmissionResult.Fail("Этот код уже был использован");

        // Начисляем баллы
        var points = token.Points > 0 ? token.Points : 20;

        // Занять токен атомарным UPDATE: параллельные сабмиты одного кода
        // (дабл-тап, два устройства) не должны начислить баллы дважды.
        // Начисление и отметка токена коммитятся вместе.
        await using var transactionScope = await db.Database.BeginTransactionAsync(ct);

        DateTime redeemedAt = DateTime.UtcNow;
        int claimed = await db.QrTokens
            .Where(t => t.Id == token.Id && t.RedeemedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.RedeemedAt, redeemedAt)
                .SetProperty(t => t.RedeemedById, playerId), ct);

        if (claimed == 0)
        {
            await transactionScope.RollbackAsync(ct);
            return SubmissionResult.Fail("Этот код уже был использован");
        }

        var wallet = await db.Wallets.SingleOrDefaultAsync(w => w.UserId == playerId, ct);
        if (wallet is null)
        {
            wallet = new Domain.Entities.Wallet { UserId = playerId, Balance = points };
            db.Wallets.Add(wallet);
        }
        else
        {
            // Условный UPDATE вместо чтения баланса в память: гонок с другими начислениями нет
            await db.Wallets
                .Where(w => w.Id == wallet.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(w => w.Balance, w => w.Balance + points), ct);
        }

        var transaction = new WalletTransaction
        {
            WalletId = wallet.Id,
            Amount = points,
            Type = WalletTransactionType.QrBonus,
            Description = $"QR-код: {submittedCode}",
            RelatedSessionId = session.Id
        };
        db.WalletTransactions.Add(transaction);

        await db.SaveChangesAsync(ct);
        await transactionScope.CommitAsync(ct);

        int newBalance = await db.Wallets.AsNoTracking()
            .Where(w => w.Id == wallet.Id)
            .Select(w => w.Balance)
            .SingleAsync(ct);

        await _pointsAward.NotifyBalanceChangedAsync(
            playerId,
            newBalance,
            points,
            $"QR-код: {submittedCode}",
            ct);

        // Большой экран и админка обновляют статистику QR без перезагрузки
        await _hub.Clients.All.SendAsync("QrRedeemed", new
        {
            playerId,
            code = submittedCode,
            points
        }, ct);

        _logger.LogInformation(
            "Player {PlayerId} redeemed QR token {Code} for {Points} points",
            playerId, submittedCode, points);

        return SubmissionResult.Ok(
            points,
            $"QR-код активирован! +{points} баллов",
            new { code = submittedCode, points });
    }
}