using PartyApp.Api.Modules.Events.Services;
using PartyApp.Domain.Entities;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Events.Handlers;

/// <summary>
/// Обработчик ивента "bingo". Игра в два этапа: сначала игроки выбирают клетки-предсказания
/// (не больше maxPredictions, но не больше половины поля), затем админ закрывает приём,
/// после чего подтверждает случившиеся события и отклоняет те, которых не было
/// (см. <see cref="BingoService"/>).
/// </summary>
public class BingoHandler : IEventHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BingoService _bingo;
    private readonly ILogger<BingoHandler> _logger;

    public BingoHandler(
        IServiceScopeFactory scopeFactory,
        BingoService bingo,
        ILogger<BingoHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _bingo = bingo;
        _logger = logger;
    }

    public string EventType => "bingo";

    public string DefaultConfigJson => """
        {
            "size": 5,
            "pointsPerCell": 5,
            "lineBonus": 10,
            "maxPredictions": 12,
            "cells": [
                "Именинник скажет тост", "Кто-то опрокинет напиток", "Прозвучит песня 2000-х", "Кто-то уснёт до полуночи", "Будет общее фото",
                "Кто-то принесёт торт", "Будет спор о музыке", "Кто-то выйдет на улицу покурить", "Именинника обнимут 10 раз", "Кто-то расскажет историю из детства",
                "Будет танцевальный баттл", "Кто-то скажет «а помнишь…»", "Раздастся смех до слёз", "Кто-то попросит добавки", "Будет запущено конфетти",
                "Кто-то сделает селфи", "Прозвучит комплимент имениннику", "Кто-то спрячет телефон", "Будет тост за родителей", "Кто-то не найдёт свой бокал",
                "Разговор про работу", "Кто-то предложит сыграть в игру", "Будет караоке", "Кто-то уйдёт «на пять минут»", "Именинник загадает желание"
            ]
        }
        """;

    public async Task<SubmissionResult> HandleSubmissionAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        string payloadJson,
        CancellationToken ct = default)
    {
        BingoService.BingoConfig config = BingoService.ParseConfig(definition.ConfigJson);
        int totalCells = config.Size * config.Size;

        if (config.Cells.Count < totalCells)
            return SubmissionResult.Fail($"Бинго не настроено: нужно {totalCells} клеток");

        int cellIndex = BingoService.ExtractCellIndex(payloadJson) ?? -1;
        if (cellIndex < 0 || cellIndex >= totalCells)
            return SubmissionResult.Fail("Неверная клетка");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (await BingoService.IsLockedAsync(db, session.Id, ct))
            return SubmissionResult.Fail("Приём предсказаний закрыт — ведущий уже перешёл к оценке событий");

        Dictionary<int, DateTime> marks = await BingoService.GetPlayerMarkTimesAsync(db, session.Id, playerId, ct);

        if (marks.ContainsKey(cellIndex))
            return SubmissionResult.Fail("Уже отмечено");

        if (marks.Count >= config.MaxPredictions)
        {
            return SubmissionResult.Fail(
                $"Лимит выбора: {config.MaxPredictions} событий. Нажми на свою отметку, чтобы снять её");
        }

        _logger.LogInformation(
            "Bingo: player {PlayerId} marked cell {CellIndex} in session {SessionId}",
            playerId, cellIndex, session.Id);

        return SubmissionResult.Ok(
            0,
            $"Предсказание принято! Выбрано {marks.Count + 1} из {config.MaxPredictions}",
            new
            {
                markedCells = marks.Keys.Append(cellIndex).OrderBy(i => i).ToArray(),
                selectedCount = marks.Count + 1,
                pendingCount = marks.Count + 1,
                maxPredictions = config.MaxPredictions
            });
    }

    /// <summary>После отметки проверяем, не закрыл ли игрок линию (см. BingoService).</summary>
    public Task AfterSubmissionAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        CancellationToken ct = default)
    {
        return _bingo.CheckLineAwardsAsync(session.Id, ct);
    }

    public Task<object?> GetLiveDataAsync(EventSession session, EventDefinition definition, CancellationToken ct = default)
    {
        return _bingo.GetLiveStateAsync(session.Id, ct);
    }

    public Task<object?> GetPlayerDataAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        CancellationToken ct = default)
    {
        return _bingo.GetPlayerStateAsync(session.Id, playerId, ct);
    }
}