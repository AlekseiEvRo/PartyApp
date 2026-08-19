using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
    private readonly ILogger<QrScanHandler> _logger;

    public QrScanHandler(IServiceScopeFactory scopeFactory, ILogger<QrScanHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public string EventType => "qr_scan";

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
            .FirstOrDefaultAsync(t => t.Code == submittedCode, ct);

        if (token is null)
            return SubmissionResult.Fail("Код не найден");

        if (token.RedeemedAt.HasValue)
            return SubmissionResult.Fail("Этот код уже был использован");

        // Начисляем баллы
        var points = token.Points > 0 ? token.Points : 20;

        var wallet = await db.Wallets.SingleOrDefaultAsync(w => w.UserId == playerId, ct);
        if (wallet is null)
        {
            wallet = new Domain.Entities.Wallet { UserId = playerId, Balance = points };
            db.Wallets.Add(wallet);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            wallet.Balance += points;
            await db.SaveChangesAsync(ct);
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

        // Помечаем токен как использованный
        token.RedeemedById = playerId;
        token.RedeemedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Player {PlayerId} redeemed QR token {Code} for {Points} points",
            playerId, submittedCode, points);

        return SubmissionResult.Ok(
            points,
            $"QR-код активирован! +{points} баллов",
            new { code = submittedCode, points });
    }
}