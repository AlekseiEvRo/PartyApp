using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Events.Handlers;

/// <summary>
/// Обработчик ивента "bingo". Игроки отмечают клетки-предсказания;
/// за клетку — баллы, за собранную линию (строку, столбец, диагональ) — бонус.
/// Линии считаются на сервере по сохранённым отметкам.
/// </summary>
public class BingoHandler : IEventHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPointsAwardService _pointsAward;
    private readonly ILogger<BingoHandler> _logger;

    public BingoHandler(
        IServiceScopeFactory scopeFactory,
        IPointsAwardService pointsAward,
        ILogger<BingoHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _pointsAward = pointsAward;
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
        var config = JsonSerializer.Deserialize<BingoConfig>(definition.ConfigJson, EventJsonOptions.Default);
        int size = config?.Size is >= 3 and <= 7 ? config.Size : 5;
        int totalCells = size * size;
        var cells = config?.Cells ?? new List<string>();
        int pointsPerCell = config?.PointsPerCell > 0 ? config.PointsPerCell : 1;
        int lineBonus = config?.LineBonus > 0 ? config.LineBonus : 10;

        if (cells.Count < totalCells)
            return SubmissionResult.Fail($"Бинго не настроено: нужно {totalCells} клеток");

        int cellIndex = -1;
        if (!string.IsNullOrEmpty(payloadJson))
        {
            try
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson, EventJsonOptions.Default);
                if (payload.TryGetProperty("cellIndex", out var indexProp))
                    cellIndex = indexProp.GetInt32();
            }
            catch { /* ignore */ }
        }

        if (cellIndex < 0 || cellIndex >= totalCells)
            return SubmissionResult.Fail("Неверная клетка");

        HashSet<int> marked = await GetMarkedCellsAsync(session.Id, playerId, ct);

        if (marked.Contains(cellIndex))
            return SubmissionResult.Fail("Уже отмечено");

        marked.Add(cellIndex);

        int newLines = CountLinesThrough(marked, cellIndex, size);
        int points = pointsPerCell + newLines * lineBonus;

        await _pointsAward.AwardAsync(
            playerId,
            points,
            newLines > 0 ? $"Бинго: линия (+{newLines * lineBonus})" : "Бинго: клетка",
            sessionId: session.Id,
            ct: ct);

        _logger.LogInformation(
            "Bingo: player {PlayerId} marked cell {CellIndex} in session {SessionId}",
            playerId, cellIndex, session.Id);

        return SubmissionResult.Ok(
            points,
            newLines > 0 ? $"🎉 Линия! Бонус +{newLines * lineBonus}" : "Клетка отмечена",
            new
            {
                markedCells = marked.OrderBy(i => i).ToArray(),
                lines = CountAllLines(marked, size),
                newLines
            });
    }

    public async Task<object?> GetLiveDataAsync(
        EventSession session,
        EventDefinition definition,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        int markedCount = await db.PlayerSubmissions
            .CountAsync(s => s.SessionId == session.Id && s.Score > 0, ct);

        int playersCount = await db.PlayerSubmissions
            .Where(s => s.SessionId == session.Id && s.Score > 0)
            .Select(s => s.PlayerId)
            .Distinct()
            .CountAsync(ct);

        return new { markedCount, playersCount };
    }

    public async Task<object?> GetPlayerDataAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        CancellationToken ct = default)
    {
        var config = JsonSerializer.Deserialize<BingoConfig>(definition.ConfigJson, EventJsonOptions.Default);
        int size = config?.Size is >= 3 and <= 7 ? config.Size : 5;

        HashSet<int> marked = await GetMarkedCellsAsync(session.Id, playerId, ct);

        return new
        {
            markedCells = marked.OrderBy(i => i).ToArray(),
            lines = CountAllLines(marked, size)
        };
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
            try
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson, EventJsonOptions.Default);
                if (payload.TryGetProperty("cellIndex", out var indexProp) && indexProp.TryGetInt32(out int index))
                    marked.Add(index);
            }
            catch { /* ignore */ }
        }

        return marked;
    }

    /// <summary>Линии, проходящие через клетку и полностью отмеченные сейчас.</summary>
    private static int CountLinesThrough(HashSet<int> marked, int cellIndex, int size)
    {
        return AllLines(size)
            .Count(line => line.Contains(cellIndex) && line.All(marked.Contains));
    }

    private static int CountAllLines(HashSet<int> marked, int size)
    {
        return AllLines(size).Count(line => line.All(marked.Contains));
    }

    private static IEnumerable<int[]> AllLines(int size)
    {
        for (int row = 0; row < size; row++)
            yield return Enumerable.Range(row * size, size).ToArray();

        for (int col = 0; col < size; col++)
            yield return Enumerable.Range(0, size).Select(row => row * size + col).ToArray();

        yield return Enumerable.Range(0, size).Select(i => i * size + i).ToArray();
        yield return Enumerable.Range(0, size).Select(i => i * size + (size - 1 - i)).ToArray();
    }

    private class BingoConfig
    {
        public int Size { get; set; } = 5;
        public int PointsPerCell { get; set; } = 1;
        public int LineBonus { get; set; } = 10;
        public List<string> Cells { get; set; } = new();
    }
}