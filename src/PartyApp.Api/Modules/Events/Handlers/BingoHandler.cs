using Microsoft.EntityFrameworkCore;
using PartyApp.Api.Modules.Events.Services;
using PartyApp.Domain.Entities;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Events.Handlers;

/// <summary>
/// Обработчик ивента "bingo". Игроки отмечают клетки-предсказания;
/// баллы начисляются только за клетки, которые админ подтвердил как
/// реально случившиеся события (см. <see cref="BingoService"/>).
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
            "pointsPerCell": 1,
            "lineBonus": 10,
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

        HashSet<int> marked = await GetMarkedCellsAsync(session.Id, playerId, ct);

        if (marked.Contains(cellIndex))
            return SubmissionResult.Fail("Уже отмечено");

        marked.Add(cellIndex);

        _logger.LogInformation(
            "Bingo: player {PlayerId} marked cell {CellIndex} in session {SessionId}",
            playerId, cellIndex, session.Id);

        // Баллы не начисляем: их даст админ, когда подтвердит, что событие было
        return SubmissionResult.Ok(
            0,
            "Отмечено! Ждём подтверждения ведущего",
            new { markedCells = marked.OrderBy(i => i).ToArray() });
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

    private async Task<HashSet<int>> GetMarkedCellsAsync(Guid sessionId, Guid playerId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        List<string> payloads = await db.PlayerSubmissions
            .Where(s => s.SessionId == sessionId && s.PlayerId == playerId)
            .Select(s => s.PayloadJson)
            .ToListAsync(ct);

        var marked = new HashSet<int>();

        foreach (string payloadJson in payloads)
        {
            int? cell = BingoService.ExtractCellIndex(payloadJson);
            if (cell is not null)
                marked.Add(cell.Value);
        }

        return marked;
    }
}