using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using PartyApp.Api.Modules.Events.Handlers;
using PartyApp.Api.Modules.Events.Services;
using PartyApp.Api.Modules.Wallet;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Events.Handlers;

/// <summary>
/// Админка подставляет DefaultConfigJson как заготовку при создании ивента,
/// поэтому каждый дефолт должен быть валидным JSON-объектом с нужными полями.
/// </summary>
public class EventHandlerDefaultConfigTests
{
    private static readonly IServiceScopeFactory ScopeFactory = Substitute.For<IServiceScopeFactory>();
    private static readonly IPointsAwardService PointsAward = Substitute.For<IPointsAwardService>();

    private static JsonElement Parse(string defaultConfigJson)
    {
        JsonDocument document = JsonDocument.Parse(defaultConfigJson);
        document.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        return document.RootElement.Clone();
    }

    [Fact]
    public void QuizHandler_DefaultConfig_HasValidQuestions()
    {
        QuizHandler handler = new(
            ScopeFactory,
            PointsAward,
            NullLogger<QuizHandler>.Instance);

        JsonElement config = Parse(handler.DefaultConfigJson);

        config.GetProperty("pointsPerCorrect").GetInt32().Should().BePositive();
        JsonElement questions = config.GetProperty("questions");
        questions.GetArrayLength().Should().BePositive();

        foreach (JsonElement question in questions.EnumerateArray())
        {
            question.GetProperty("text").GetString().Should().NotBeNullOrWhiteSpace();
            int optionsCount = question.GetProperty("options").GetArrayLength();
            optionsCount.Should().BeGreaterThanOrEqualTo(2);

            int correctIndex = question.GetProperty("correctIndex").GetInt32();
            correctIndex.Should().BeInRange(0, optionsCount - 1);
        }
    }

    [Fact]
    public void WordRushHandler_DefaultConfig_HasRequiredLettersAndPoints()
    {
        WordRushHandler handler = new(
            ScopeFactory,
            new RussianDictionaryService(TestConfiguration.Empty(), NullLogger<RussianDictionaryService>.Instance),
            PointsAward,
            NullLogger<WordRushHandler>.Instance);

        JsonElement config = Parse(handler.DefaultConfigJson);

        config.GetProperty("requiredLetters").GetArrayLength().Should().BePositive();
        config.GetProperty("minWordLength").GetInt32().Should().BeGreaterThanOrEqualTo(3);
        config.GetProperty("pointsPerWord").GetInt32().Should().BePositive();
        config.GetProperty("timeLimitSec").GetInt32().Should().BePositive();
    }

    [Fact]
    public void PromoCodeHandler_DefaultConfig_HasCodesAndPoints()
    {
        PromoCodeHandler handler = new(
            ScopeFactory,
            PointsAward,
            NullLogger<PromoCodeHandler>.Instance);

        JsonElement config = Parse(handler.DefaultConfigJson);

        JsonElement codes = config.GetProperty("codes");
        codes.GetArrayLength().Should().BePositive();
        foreach (JsonElement code in codes.EnumerateArray())
        {
            code.GetString().Should().NotBeNullOrWhiteSpace();
        }

        config.GetProperty("pointsPerCode").GetInt32().Should().BePositive();
        config.GetProperty("oneTimePerPlayer").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void QuickCheckinHandler_DefaultConfig_HasPointsAndCooldown()
    {
        QuickCheckinHandler handler = new(
            PointsAward,
            new RecordingHubContext(),
            TimeProvider.System,
            NullLogger<QuickCheckinHandler>.Instance);

        JsonElement config = Parse(handler.DefaultConfigJson);

        config.GetProperty("points").GetInt32().Should().BePositive();
        config.GetProperty("cooldownSeconds").GetInt32().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void QrScanHandler_DefaultConfig_IsEmptyJsonObject()
    {
        QrScanHandler handler = new(
            ScopeFactory,
            PointsAward,
            NullLogger<QrScanHandler>.Instance);

        JsonElement config = Parse(handler.DefaultConfigJson);

        config.EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public void AllHandlers_ExposeUniqueEventTypeAndValidDefaultConfig()
    {
        IEventHandler[] handlers =
        {
            new QuizHandler(ScopeFactory, PointsAward, NullLogger<QuizHandler>.Instance),
            new WordRushHandler(
                ScopeFactory,
                new RussianDictionaryService(TestConfiguration.Empty(), NullLogger<RussianDictionaryService>.Instance),
                PointsAward,
                NullLogger<WordRushHandler>.Instance),
            new PromoCodeHandler(ScopeFactory, PointsAward, NullLogger<PromoCodeHandler>.Instance),
            new QrScanHandler(ScopeFactory, PointsAward, NullLogger<QrScanHandler>.Instance),
            new QuickCheckinHandler(
                PointsAward,
                new RecordingHubContext(),
                TimeProvider.System,
                NullLogger<QuickCheckinHandler>.Instance)
        };

        handlers.Select(h => h.EventType).Should().OnlyHaveUniqueItems();
        handlers.Should().OnlyContain(h => !string.IsNullOrWhiteSpace(h.EventType));

        foreach (IEventHandler handler in handlers)
        {
            Action act = () => Parse(handler.DefaultConfigJson);
            act.Should().NotThrow($"{handler.GetType().Name} must expose a valid default config");
        }
    }
}