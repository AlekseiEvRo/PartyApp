using Microsoft.EntityFrameworkCore;
using PartyApp.Domain.Entities;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Events.Services;

public interface IQrTokenService
{
    Task<List<QrTokenDto>> GetTokensAsync(CancellationToken ct = default);
    Task<List<QrTokenDto>> GenerateTokensAsync(int count, int points, CancellationToken ct = default);
    Task<QrStatsDto> GetStatsAsync(CancellationToken ct = default);
}

public record QrTokenDto(
    Guid Id,
    string Code,
    int Points,
    bool IsRedeemed,
    DateTime? RedeemedAt,
    string? RedeemedByName);

public record QrStatsDto(int Total, int Redeemed, int Remaining, List<QrPlayerStatDto> Players);

public record QrPlayerStatDto(Guid PlayerId, string DisplayName, int Count);

public class QrTokenService : IQrTokenService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<QrTokenService> _logger;

    private const string CodeChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // Без похожих символов (0/O, 1/I)
    private const int CodeLength = 6;

    public QrTokenService(IServiceScopeFactory scopeFactory, ILogger<QrTokenService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<List<QrTokenDto>> GetTokensAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.QrTokens
            .OrderBy(t => t.CreatedAt)
            .Select(t => new QrTokenDto(
                t.Id,
                t.Code,
                t.Points,
                t.RedeemedAt.HasValue,
                t.RedeemedAt,
                t.RedeemedBy != null ? t.RedeemedBy.DisplayName : null))
            .ToListAsync(ct);
    }

    public async Task<List<QrTokenDto>> GenerateTokensAsync(int count, int points, CancellationToken ct = default)
    {
        if (count < 1 || count > 100)
            throw new InvalidOperationException("Количество токенов должно быть от 1 до 100");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existingCodes = await db.QrTokens.Select(t => t.Code).ToListAsync(ct);
        var existingSet = new HashSet<string>(existingCodes);

        var newTokens = new List<QrToken>();

        for (int i = 0; i < count; i++)
        {
            string code;
            do
            {
                code = GenerateCode();
            } while (existingSet.Contains(code));

            existingSet.Add(code);

            var token = new QrToken
            {
                Code = code,
                Points = points,
                CreatedAt = DateTime.UtcNow
            };

            newTokens.Add(token);
        }

        db.QrTokens.AddRange(newTokens);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Generated {Count} QR tokens with {Points} points each", count, points);

        return newTokens.Select(t => new QrTokenDto(t.Id, t.Code, t.Points, false, null, null)).ToList();
    }

    public async Task<QrStatsDto> GetStatsAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        int total = await db.QrTokens.CountAsync(ct);
        int redeemed = await db.QrTokens.CountAsync(t => t.RedeemedAt != null, ct);

        // «Верные» коды — только успешно активированные: неудачные попытки нигде не хранятся
        List<QrPlayerStatDto> players = await db.QrTokens
            .Where(t => t.RedeemedById != null && t.RedeemedBy != null)
            .GroupBy(t => new { t.RedeemedById, t.RedeemedBy!.DisplayName })
            .Select(g => new QrPlayerStatDto(g.Key.RedeemedById!.Value, g.Key.DisplayName, g.Count()))
            .ToListAsync(ct);

        players = players
            .OrderByDescending(p => p.Count)
            .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new QrStatsDto(total, redeemed, total - redeemed, players);
    }

    private static string GenerateCode()
    {
        var random = new Random();
        var chars = new char[CodeLength];
        for (int i = 0; i < CodeLength; i++)
        {
            chars[i] = CodeChars[random.Next(CodeChars.Length)];
        }
        return new string(chars);
    }
}