using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

using PartyApp.Api.Hubs;
using PartyApp.Api.Modules.Events.Services;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Polls;

/// <summary>
/// Голосование за следующий ивент: создать (предыдущее закрывается), проголосовать
/// с возможностью передумать, закрыть и запустить победителя.
/// </summary>
public class PollService
{
    private readonly AppDbContext _db;
    private readonly IEventService _events;
    private readonly IHubContext<PartyHub> _hub;
    private readonly ILogger<PollService> _logger;

    public PollService(
        AppDbContext db,
        IEventService events,
        IHubContext<PartyHub> hub,
        ILogger<PollService> logger)
    {
        _db = db;
        _events = events;
        _hub = hub;
        _logger = logger;
    }

    public async Task<PollDto?> GetCurrentAsync(Guid? userId, CancellationToken ct = default)
    {
        Poll? poll = await _db.Polls.AsNoTracking()
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct);

        return poll is null ? null : await ToDtoAsync(poll.Id, userId, ct);
    }

    public async Task<PollDto> CreateAsync(
        string question,
        IReadOnlyList<Guid> definitionIds,
        Guid adminId,
        CancellationToken ct = default)
    {
        // Открытое голосование может быть только одно
        Poll? previous = await _db.Polls.FirstOrDefaultAsync(p => p.Status == PollStatus.Open, ct);
        if (previous is not null)
        {
            previous.Status = PollStatus.Closed;
            previous.ClosedAt = DateTime.UtcNow;
        }

        var poll = new Poll
        {
            Question = question.Trim(),
            Status = PollStatus.Open,
            CreatedAt = DateTime.UtcNow,
            CreatedById = adminId
        };

        for (int i = 0; i < definitionIds.Count; i++)
        {
            poll.Options.Add(new PollOption
            {
                DefinitionId = definitionIds[i],
                Order = i
            });
        }

        _db.Polls.Add(poll);
        await _db.SaveChangesAsync(ct);

        PollDto dto = (await ToDtoAsync(poll.Id, userId: null, ct))!;
        await _hub.Clients.All.SendAsync("PollUpdated", dto, ct);

        _logger.LogInformation("Poll created: {PollId}, options={Options}", poll.Id, definitionIds.Count);
        return dto;
    }

    public async Task<(PollDto? Poll, string? Error)> VoteAsync(
        Guid pollId,
        Guid userId,
        Guid optionId,
        CancellationToken ct = default)
    {
        Poll? poll = await _db.Polls.FirstOrDefaultAsync(p => p.Id == pollId, ct);
        if (poll is null)
            return (null, "Голосование не найдено");

        if (poll.Status != PollStatus.Open)
            return (null, "Голосование уже закрыто");

        bool optionValid = await _db.PollOptions.AnyAsync(o => o.Id == optionId && o.PollId == pollId, ct);
        if (!optionValid)
            return (null, "Такого варианта нет");

        PollVote? vote = await _db.PollVotes
            .FirstOrDefaultAsync(v => v.PollId == pollId && v.UserId == userId, ct);

        if (vote is null)
        {
            _db.PollVotes.Add(new PollVote
            {
                PollId = pollId,
                OptionId = optionId,
                UserId = userId
            });
        }
        else
        {
            // До закрытия можно передумать
            vote.OptionId = optionId;
        }

        await _db.SaveChangesAsync(ct);

        PollDto personal = (await ToDtoAsync(pollId, userId, ct))!;

        // Всем уходит обезличенный DTO: личный выбор игрока — только ему в ответе
        await _hub.Clients.All.SendAsync("PollUpdated", personal with { MyOptionId = null }, ct);

        return (personal, null);
    }

    public async Task<(PollDto? Poll, string? Error)> CloseAsync(Guid pollId, CancellationToken ct = default)
    {
        Poll? poll = await _db.Polls.FirstOrDefaultAsync(p => p.Id == pollId, ct);
        if (poll is null)
            return (null, "Голосование не найдено");

        if (poll.Status == PollStatus.Open)
        {
            poll.Status = PollStatus.Closed;
            poll.ClosedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        PollDto dto = (await ToDtoAsync(pollId, userId: null, ct))!;
        await _hub.Clients.All.SendAsync("PollClosed", dto, ct);

        _logger.LogInformation("Poll closed: {PollId}", pollId);
        return (dto, null);
    }

    /// <summary>Запускает победивший ивент (голосование при необходимости закрывается).</summary>
    public async Task<(Guid? SessionId, string? Error)> StartWinnerAsync(
        Guid pollId,
        Guid adminId,
        CancellationToken ct = default)
    {
        Poll? poll = await _db.Polls.FirstOrDefaultAsync(p => p.Id == pollId, ct);
        if (poll is null)
            return (null, "Голосование не найдено");

        PollDto dto = (await ToDtoAsync(pollId, userId: null, ct))!;
        if (dto.WinnerOptionId is null)
            return (null, "В голосовании пока нет голосов");

        PollOption winner = await _db.PollOptions
            .AsNoTracking()
            .SingleAsync(o => o.Id == dto.WinnerOptionId.Value, ct);

        if (poll.Status == PollStatus.Open)
        {
            poll.Status = PollStatus.Closed;
            poll.ClosedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            PollDto closed = (await ToDtoAsync(pollId, userId: null, ct))!;
            await _hub.Clients.All.SendAsync("PollClosed", closed, ct);
        }

        EventSession session;
        try
        {
            session = await _events.StartEventAsync(winner.DefinitionId, adminId, ct);
        }
        catch (InvalidOperationException ex)
        {
            return (null, ex.Message);
        }

        _logger.LogInformation(
            "Poll winner started: poll={PollId}, definition={DefinitionId}, session={SessionId}",
            pollId,
            winner.DefinitionId,
            session.Id);

        return (session.Id, null);
    }

    private async Task<PollDto?> ToDtoAsync(Guid pollId, Guid? userId, CancellationToken ct)
    {
        Poll? poll = await _db.Polls.AsNoTracking()
            .Include(p => p.Options)
            .ThenInclude(o => o.Definition)
            .FirstOrDefaultAsync(p => p.Id == pollId, ct);

        if (poll is null)
            return null;

        Dictionary<Guid, int> voteCounts = await _db.PollVotes.AsNoTracking()
            .Where(v => v.PollId == pollId)
            .GroupBy(v => v.OptionId)
            .Select(g => new { OptionId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.OptionId, x => x.Count, ct);

        Guid? myOptionId = null;
        if (userId.HasValue)
        {
            myOptionId = await _db.PollVotes.AsNoTracking()
                .Where(v => v.PollId == pollId && v.UserId == userId.Value)
                .Select(v => (Guid?)v.OptionId)
                .FirstOrDefaultAsync(ct);
        }

        List<PollOptionDto> options = poll.Options
            .OrderBy(o => o.Order)
            .Select(o => new PollOptionDto(
                o.Id,
                o.DefinitionId,
                o.Definition.DisplayName,
                o.Definition.Type,
                voteCounts.GetValueOrDefault(o.Id)))
            .ToList();

        int totalVotes = options.Sum(o => o.Votes);

        Guid? winnerOptionId = totalVotes == 0
            ? null
            : options
                .Select((option, index) => new { option, index })
                .OrderByDescending(x => x.option.Votes)
                .ThenBy(x => x.index)
                .First()
                .option.Id;

        return new PollDto(
            poll.Id,
            poll.Question,
            poll.Status.ToString(),
            poll.CreatedAt,
            poll.ClosedAt,
            options,
            myOptionId,
            totalVotes,
            winnerOptionId);
    }
}

public record PollOptionDto(
    Guid Id,
    Guid DefinitionId,
    string DisplayName,
    string Type,
    int Votes);

public record PollDto(
    Guid Id,
    string Question,
    string Status,
    DateTime CreatedAt,
    DateTime? ClosedAt,
    List<PollOptionDto> Options,
    Guid? MyOptionId,
    int TotalVotes,
    Guid? WinnerOptionId);
