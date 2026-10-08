using System.Text.Json;

using PartyApp.Api.Modules.Events.Services;
using PartyApp.Domain.Entities;

namespace PartyApp.Api.Modules.Events.Handlers;

/// <summary>
/// Обработчик ивента "spyfall" («Шпионы»). Админ выбирает участников в отдельной
/// вкладке, сервер выдаёт роли и слова (см. <see cref="SpyFallService"/>).
/// Горожанин голосует через submit с payload {"vote":"&lt;guid&gt;"},
/// шпион один раз пробует угадать слово: {"guess":"..."}.
/// </summary>
public class SpyFallHandler : IEventHandler
{
    private readonly SpyFallService _spyFall;

    public SpyFallHandler(SpyFallService spyFall)
    {
        _spyFall = spyFall;
    }

    public string EventType => "spyfall";

    /// <summary>Игра стартует только из вкладки «Шпионы» — там выбирают состав.</summary>
    public bool RequiresCustomStart => true;

    public string DefaultConfigJson => """
        {
            "pointsSpy": 50,
            "pointsCitizen": 25,
            "pairs": [
                { "citizen": "торт", "spy": "пирожное" },
                { "citizen": "шампанское", "spy": "игристое вино" },
                { "citizen": "гитара", "spy": "скрипка" },
                { "citizen": "свадьба", "spy": "юбилей" },
                { "citizen": "дискотека", "spy": "корпоратив" },
                { "citizen": "пицца", "spy": "паста" },
                { "citizen": "караоке", "spy": "стендап" },
                { "citizen": "фотоаппарат", "spy": "смартфон" },
                { "citizen": "отпуск", "spy": "выходные" },
                { "citizen": "пляж", "spy": "бассейн" },
                { "citizen": "Москва", "spy": "Санкт-Петербург" },
                { "citizen": "такси", "spy": "автобус" },
                { "citizen": "чай", "spy": "кофе" },
                { "citizen": "кот", "spy": "собака" },
                { "citizen": "футбол", "spy": "хоккей" },
                { "citizen": "шахматы", "spy": "шашки" },
                { "citizen": "врач", "spy": "медсестра" },
                { "citizen": "пилот", "spy": "стюардесса" },
                { "citizen": "официант", "spy": "бармен" },
                { "citizen": "учитель", "spy": "репетитор" },
                { "citizen": "Дед Мороз", "spy": "Санта-Клаус" },
                { "citizen": "фейерверк", "spy": "салют" },
                { "citizen": "подарок", "spy": "сюрприз" },
                { "citizen": "кино", "spy": "театр" },
                { "citizen": "пикник", "spy": "шашлыки" },
                { "citizen": "барабан", "spy": "бубен" },
                { "citizen": "вампир", "spy": "зомби" },
                { "citizen": "самолёт", "spy": "поезд" },
                { "citizen": "мороженое", "spy": "сорбет" },
                { "citizen": "настольная игра", "spy": "пазл" },
                { "citizen": "бокал", "spy": "кружка" }
            ]
        }
        """;

    /// <summary>Пары слов — секрет: игрокам конфиг не отдаём.</summary>
    public object? GetPublicConfig(EventDefinition definition) => null;

    public Task<bool> IsPlayerAllowedAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        CancellationToken ct = default)
    {
        return _spyFall.IsParticipantAsync(session.Id, playerId, ct);
    }

    public async Task<SubmissionResult> HandleSubmissionAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        string payloadJson,
        CancellationToken ct = default)
    {
        string? vote = null;
        string? guess = null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(payloadJson);

            if (document.RootElement.TryGetProperty("vote", out JsonElement voteElement)
                && voteElement.ValueKind == JsonValueKind.String)
            {
                vote = voteElement.GetString();
            }
            else if (document.RootElement.TryGetProperty("guess", out JsonElement guessElement)
                && guessElement.ValueKind == JsonValueKind.String)
            {
                guess = guessElement.GetString();
            }
        }
        catch (JsonException)
        {
            return SubmissionResult.Fail("Некорректный запрос");
        }

        if (vote is not null)
        {
            if (!Guid.TryParse(vote, out Guid targetId))
                return SubmissionResult.Fail("Выбери игрока из списка");

            SpyFallOutcome outcome = await _spyFall.VoteAsync(session.Id, playerId, targetId, ct);

            return outcome.Success
                ? SubmissionResult.Ok(0, outcome.Message, outcome.Data)
                : SubmissionResult.Fail(outcome.Message, outcome.Data);
        }

        if (guess is not null)
        {
            SpyFallOutcome outcome = await _spyFall.GuessAsync(session.Id, playerId, guess, ct);

            return outcome.Success
                ? SubmissionResult.Ok(outcome.PointsAwarded, outcome.Message, outcome.Data)
                : SubmissionResult.Fail(outcome.Message, outcome.Data);
        }

        return SubmissionResult.Fail("Непонятное действие");
    }

    /// <summary>Раунд не завершён к моменту закрытия ивента — раскрываем без баллов.</summary>
    public Task OnSessionFinishedAsync(EventSession session, EventDefinition definition, CancellationToken ct = default)
    {
        return _spyFall.CloseUnresolvedAsync(session.Id, ct);
    }

    public Task<object?> GetLiveDataAsync(EventSession session, EventDefinition definition, CancellationToken ct = default)
    {
        return _spyFall.GetLiveStateAsync(session.Id, ct);
    }

    public Task<object?> GetPlayerDataAsync(
        EventSession session,
        EventDefinition definition,
        Guid playerId,
        CancellationToken ct = default)
    {
        return _spyFall.GetPlayerStateAsync(session.Id, playerId, ct);
    }
}