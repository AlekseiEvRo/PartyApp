using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

using PartyApp.Api.Hubs;
using PartyApp.Api.Modules.Events.Services;
using PartyApp.Api.Modules.Push;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Parties;

/// <summary>
/// Вечеринки: старт новой смены с обнулением баллов, завершение и итоги.
/// Итоги считаются по PartyId (сессии, фото, пожелания) и по времени вечеринки
/// для заработка — партии не пересекаются, поэтому границы точные.
/// </summary>
public class PartyService
{
    private readonly AppDbContext _db;
    private readonly IEventService _events;
    private readonly IPointsAwardService _pointsAward;
    private readonly IHubContext<PartyHub> _hub;
    private readonly IPushNotificationService _push;
    private readonly ILogger<PartyService> _logger;

    public PartyService(
        AppDbContext db,
        IEventService events,
        IPointsAwardService pointsAward,
        IHubContext<PartyHub> hub,
        IPushNotificationService push,
        ILogger<PartyService> logger)
    {
        _db = db;
        _events = events;
        _pointsAward = pointsAward;
        _hub = hub;
        _push = push;
        _logger = logger;
    }

    public Task<Party?> GetActiveAsync(CancellationToken ct = default)
    {
        return _db.Parties
            .Where(p => p.Status == PartyStatus.Active)
            .OrderByDescending(p => p.StartedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Активная вечеринка, а если её нет — последняя завершённая (для экрана итогов).</summary>
    public async Task<Party?> GetCurrentOrLatestAsync(CancellationToken ct = default)
    {
        Party? active = await GetActiveAsync(ct);
        if (active is not null)
            return active;

        return await _db.Parties
            .OrderByDescending(p => p.StartedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Начинает новую вечеринку, завершая предыдущую и все активные ивенты.</summary>
    public async Task<Party> StartAsync(string name, bool resetBalances, CancellationToken ct = default)
    {
        Party? active = await GetActiveAsync(ct);
        if (active is not null)
            await FinishInternalAsync(active, ct);
        else
            await CloseActiveEventsAsync(ct);

        var party = new Party
        {
            Name = name.Trim(),
            Status = PartyStatus.Active,
            StartedAt = DateTime.UtcNow
        };

        _db.Parties.Add(party);
        await _db.SaveChangesAsync(ct);

        if (resetBalances)
            await ResetBalancesAsync(ct);

        PartyDto dto = ToDto(party);
        await _hub.Clients.All.SendAsync("PartyStarted", dto, ct);
        await _push.SendToAllAsync(
            new PushMessage(
                Title: $"🎉 {party.Name}",
                Body: resetBalances ? "Новая вечеринка! Баллы обнулены" : "Новая вечеринка началась!",
                Url: "/",
                Tag: "party"),
            ct: ct);

        _logger.LogInformation(
            "Party started: {Name} ({PartyId}), resetBalances={ResetBalances}",
            party.Name,
            party.Id,
            resetBalances);

        return party;
    }

    /// <summary>Завершает вечеринку, рассылает итоги. Повторный вызов возвращает те же итоги.</summary>
    public async Task<PartySummaryDto?> FinishAsync(Guid partyId, CancellationToken ct = default)
    {
        Party? party = await _db.Parties.FirstOrDefaultAsync(p => p.Id == partyId, ct);
        if (party is null)
            return null;

        bool justFinished = party.Status == PartyStatus.Active;
        if (justFinished)
            await FinishInternalAsync(party, ct);

        PartySummaryDto? summary = await GetSummaryAsync(partyId, ct);
        if (summary is null)
            return null;

        if (justFinished)
        {
            await _hub.Clients.All.SendAsync("PartyFinished", summary, ct);
            _logger.LogInformation("Party finished: {Name} ({PartyId})", party.Name, party.Id);
        }

        return summary;
    }

    public async Task<PartySummaryDto?> GetSummaryAsync(Guid partyId, CancellationToken ct = default)
    {
        Party? party = await _db.Parties.AsNoTracking().FirstOrDefaultAsync(p => p.Id == partyId, ct);
        if (party is null)
            return null;

        DateTime from = party.StartedAt;
        DateTime to = party.EndedAt ?? DateTime.UtcNow;

        List<string> sessionTypes = await _db.EventSessions.AsNoTracking()
            .Where(s => s.PartyId == partyId)
            .Select(s => s.Definition.Type)
            .ToListAsync(ct);

        int submissionsCount = await _db.PlayerSubmissions.AsNoTracking()
            .CountAsync(s => s.Session.PartyId == partyId, ct);

        // Заработок за вечеринку: положительные транзакции в её временном окне
        var earnings = await _db.WalletTransactions.AsNoTracking()
            .Where(t => t.Amount > 0 && t.CreatedAt >= from && t.CreatedAt <= to)
            .GroupBy(t => t.Wallet.UserId)
            .Select(g => new { UserId = g.Key, Earned = g.Sum(t => t.Amount) })
            .OrderByDescending(x => x.Earned)
            .Take(10)
            .ToListAsync(ct);

        List<Guid> topIds = earnings.Select(e => e.UserId).ToList();
        Dictionary<Guid, string> names = await _db.Users.AsNoTracking()
            .Where(u => topIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName })
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        List<PartyTopPlayerDto> topPlayers = earnings
            .Select(e => new PartyTopPlayerDto(e.UserId, names.GetValueOrDefault(e.UserId, "Игрок"), e.Earned))
            .ToList();

        int photosCount = await _db.PartyPhotos.AsNoTracking()
            .CountAsync(p => p.PartyId == partyId, ct);

        PartyTopPhotoDto? topPhoto = await _db.PartyPhotos.AsNoTracking()
            .Where(p => p.PartyId == partyId && p.Status == ModerationStatus.Approved)
            .OrderByDescending(p => p.Likes.Count)
            .Select(p => new PartyTopPhotoDto(p.Id, p.Caption, p.UploadedBy.DisplayName, p.Likes.Count))
            .FirstOrDefaultAsync(ct);

        int wishesCount = await _db.Wishes.AsNoTracking()
            .CountAsync(w => w.PartyId == partyId, ct);

        // Участники — те, кто отвечал на ивенты или загружал фото
        HashSet<Guid> players = new();
        players.UnionWith(await _db.PlayerSubmissions.AsNoTracking()
            .Where(s => s.Session.PartyId == partyId)
            .Select(s => s.PlayerId)
            .Distinct()
            .ToListAsync(ct));
        players.UnionWith(await _db.PartyPhotos.AsNoTracking()
            .Where(p => p.PartyId == partyId)
            .Select(p => p.UploadedById)
            .Distinct()
            .ToListAsync(ct));

        Dictionary<string, int> eventsByType = sessionTypes
            .GroupBy(t => t)
            .ToDictionary(g => g.Key, g => g.Count());

        return new PartySummaryDto(
            Party: ToDto(party),
            DurationMinutes: Math.Max(0, (int)(to - from).TotalMinutes),
            PlayersCount: players.Count,
            TopPlayers: topPlayers,
            EventsCount: sessionTypes.Count,
            EventsByType: eventsByType,
            SubmissionsCount: submissionsCount,
            PhotosCount: photosCount,
            TopPhoto: topPhoto,
            WishesCount: wishesCount);
    }

    // === Сценарий вечеринки ===

    public async Task<List<ScheduleItemDto>> GetScheduleAsync(Guid partyId, CancellationToken ct = default)
    {
        return await _db.PartyScheduleItems.AsNoTracking()
            .Where(i => i.PartyId == partyId)
            .OrderBy(i => i.Order)
            .Select(i => new ScheduleItemDto(
                i.Id,
                i.DefinitionId,
                i.Definition.DisplayName,
                i.Definition.Type,
                i.Definition.Description,
                i.Order,
                i.StartedAt,
                i.SessionId))
            .ToListAsync(ct);
    }

    public Task<ScheduleItemDto?> GetNextScheduleItemAsync(Guid partyId, CancellationToken ct = default)
    {
        return _db.PartyScheduleItems.AsNoTracking()
            .Where(i => i.PartyId == partyId && i.StartedAt == null)
            .OrderBy(i => i.Order)
            .Select(i => new ScheduleItemDto(
                i.Id,
                i.DefinitionId,
                i.Definition.DisplayName,
                i.Definition.Type,
                i.Definition.Description,
                i.Order,
                i.StartedAt,
                i.SessionId))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<ScheduleItemDto> AddScheduleItemAsync(
        Guid partyId,
        EventDefinition definition,
        CancellationToken ct = default)
    {
        int nextOrder = await _db.PartyScheduleItems
            .Where(i => i.PartyId == partyId)
            .Select(i => (int?)i.Order)
            .MaxAsync(ct) ?? 0;

        var item = new PartyScheduleItem
        {
            PartyId = partyId,
            DefinitionId = definition.Id,
            Order = nextOrder + 1
        };

        _db.PartyScheduleItems.Add(item);
        await _db.SaveChangesAsync(ct);

        return new ScheduleItemDto(
            item.Id,
            definition.Id,
            definition.DisplayName,
            definition.Type,
            definition.Description,
            item.Order,
            null,
            null);
    }

    /// <summary>Меняет порядок двух соседних пунктов. false — пункт не найден.</summary>
    public async Task<bool> MoveScheduleItemAsync(Guid partyId, Guid itemId, bool moveUp, CancellationToken ct = default)
    {
        List<PartyScheduleItem> items = await _db.PartyScheduleItems
            .Where(i => i.PartyId == partyId && i.StartedAt == null)
            .OrderBy(i => i.Order)
            .ToListAsync(ct);

        int index = items.FindIndex(i => i.Id == itemId);
        if (index < 0)
            return false;

        int swapWith = moveUp ? index - 1 : index + 1;
        if (swapWith < 0 || swapWith >= items.Count)
            return true;

        (items[index].Order, items[swapWith].Order) = (items[swapWith].Order, items[index].Order);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RemoveScheduleItemAsync(Guid partyId, Guid itemId, CancellationToken ct = default)
    {
        int removed = await _db.PartyScheduleItems
            .Where(i => i.Id == itemId && i.PartyId == partyId)
            .ExecuteDeleteAsync(ct);

        return removed > 0;
    }

    /// <summary>Запускает ивент из очереди и отмечает пункт выполненным.</summary>
    public async Task<ScheduleStartOutcome> StartScheduleItemAsync(
        Guid partyId,
        Guid itemId,
        Guid adminId,
        CancellationToken ct = default)
    {
        Party? party = await _db.Parties.AsNoTracking().FirstOrDefaultAsync(p => p.Id == partyId, ct);
        if (party is null)
            return new ScheduleStartOutcome(null, "Вечеринка не найдена");

        if (party.Status != PartyStatus.Active)
            return new ScheduleStartOutcome(null, "Вечеринка уже завершена");

        PartyScheduleItem? item = await _db.PartyScheduleItems
            .FirstOrDefaultAsync(i => i.Id == itemId && i.PartyId == partyId, ct);

        if (item is null)
            return new ScheduleStartOutcome(null, "Пункт сценария не найден");

        if (item.StartedAt is not null)
            return new ScheduleStartOutcome(null, "Этот пункт уже запускали");

        EventSession session;
        try
        {
            session = await _events.StartEventAsync(item.DefinitionId, adminId, ct);
        }
        catch (InvalidOperationException ex)
        {
            return new ScheduleStartOutcome(null, ex.Message);
        }

        item.StartedAt = DateTime.UtcNow;
        item.SessionId = session.Id;
        await _db.SaveChangesAsync(ct);

        return new ScheduleStartOutcome(session.Id, null);
    }

    // === Внутреннее ===

    private async Task FinishInternalAsync(Party party, CancellationToken ct)
    {
        party.Status = PartyStatus.Finished;
        party.EndedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await CloseActiveEventsAsync(ct);
    }

    /// <summary>Активные ивенты не должны «переехать» в новую вечеринку.</summary>
    private async Task CloseActiveEventsAsync(CancellationToken ct)
    {
        List<Guid> activeSessions = await _db.EventSessions.AsNoTracking()
            .Where(s => s.State == EventSessionState.Active)
            .Select(s => s.Id)
            .ToListAsync(ct);

        foreach (Guid sessionId in activeSessions)
        {
            try
            {
                await _events.FinishEventAsync(sessionId, ct);
            }
            catch (InvalidOperationException)
            {
                // Сессию уже завершили параллельно — не страшно
            }
        }
    }

    private async Task ResetBalancesAsync(CancellationToken ct)
    {
        List<Domain.Entities.Wallet> wallets = await _db.Wallets.AsNoTracking()
            .Where(w => w.Balance != 0)
            .ToListAsync(ct);

        if (wallets.Count == 0)
            return;

        DateTime now = DateTime.UtcNow;

        foreach (Domain.Entities.Wallet wallet in wallets)
        {
            await _db.Wallets
                .Where(w => w.Id == wallet.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(w => w.Balance, 0), ct);

            _db.WalletTransactions.Add(new WalletTransaction
            {
                WalletId = wallet.Id,
                Amount = -wallet.Balance,
                Type = WalletTransactionType.PartyReset,
                Description = "Новая вечеринка",
                CreatedAt = now
            });
        }

        await _db.SaveChangesAsync(ct);

        foreach (Domain.Entities.Wallet wallet in wallets)
        {
            await _pointsAward.NotifyBalanceChangedAsync(
                wallet.UserId,
                0,
                -wallet.Balance,
                "Новая вечеринка",
                ct);
        }

        _logger.LogInformation("Balances reset for {Count} players", wallets.Count);
    }

    public static PartyDto ToDto(Party party)
    {
        return new PartyDto(
            party.Id,
            party.Name,
            party.Status.ToString(),
            party.StartedAt,
            party.EndedAt,
            party.CreatedAt);
    }
}

public record PartyDto(
    Guid Id,
    string Name,
    string Status,
    DateTime StartedAt,
    DateTime? EndedAt,
    DateTime CreatedAt);

public record PartyTopPlayerDto(Guid PlayerId, string DisplayName, int Earned);

public record PartyTopPhotoDto(Guid PhotoId, string? Caption, string UploadedByName, int LikesCount);

public record PartySummaryDto(
    PartyDto Party,
    int DurationMinutes,
    int PlayersCount,
    List<PartyTopPlayerDto> TopPlayers,
    int EventsCount,
    Dictionary<string, int> EventsByType,
    int SubmissionsCount,
    int PhotosCount,
    PartyTopPhotoDto? TopPhoto,
    int WishesCount);

public record ScheduleItemDto(
    Guid Id,
    Guid DefinitionId,
    string DisplayName,
    string Type,
    string? Description,
    int Order,
    DateTime? StartedAt,
    Guid? SessionId);

public record ScheduleStartOutcome(Guid? SessionId, string? Error);
