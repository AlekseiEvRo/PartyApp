using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using PartyApp.Api.Modules.Events.Handlers;
using PartyApp.Api.Modules.Events.Services;

namespace PartyApp.UnitTests.Events;

public class EventHandlerFactoryTests
{
    private static IEventHandler Handler(string eventType, string defaultConfigJson = "{}")
    {
        IEventHandler handler = Substitute.For<IEventHandler>();
        handler.EventType.Returns(eventType);
        handler.DefaultConfigJson.Returns(defaultConfigJson);
        return handler;
    }

    private static EventHandlerFactory CreateFactory(params IEventHandler[] handlers)
    {
        return new EventHandlerFactory(handlers, NullLogger<EventHandlerFactory>.Instance);
    }

    [Fact]
    public void GetHandler_RegisteredType_ReturnsSameInstance()
    {
        IEventHandler quiz = Handler("quiz");
        EventHandlerFactory factory = CreateFactory(quiz);

        factory.GetHandler("quiz").Should().BeSameAs(quiz);
    }

    [Fact]
    public void GetHandler_MatchesTypeCaseInsensitively()
    {
        IEventHandler quiz = Handler("quiz");
        EventHandlerFactory factory = CreateFactory(quiz);

        factory.GetHandler("QUIZ").Should().BeSameAs(quiz);
    }

    [Fact]
    public void GetHandler_UnknownType_ThrowsWithTypeInMessage()
    {
        EventHandlerFactory factory = CreateFactory(Handler("quiz"));

        Action act = () => factory.GetHandler("unknown");

        act.Should().Throw<InvalidOperationException>().WithMessage("*unknown*");
    }

    [Fact]
    public void HasHandler_MatchesCaseInsensitively()
    {
        EventHandlerFactory factory = CreateFactory(Handler("promo_code"));

        factory.HasHandler("promo_code").Should().BeTrue();
        factory.HasHandler("PROMO_CODE").Should().BeTrue();
        factory.HasHandler("quiz").Should().BeFalse();
    }

    [Fact]
    public void GetEventTypes_ReturnsEveryRegisteredKey()
    {
        EventHandlerFactory factory = CreateFactory(Handler("quiz"), Handler("word_rush"), Handler("qr_scan"));

        factory.GetEventTypes().Should().BeEquivalentTo("quiz", "word_rush", "qr_scan");
    }

    [Fact]
    public void Constructor_DuplicateType_KeepsFirstHandler()
    {
        IEventHandler first = Handler("quiz", "first");
        IEventHandler duplicate = Handler("quiz", "second");

        EventHandlerFactory factory = CreateFactory(first, duplicate);

        factory.GetEventTypes().Should().ContainSingle().Which.Should().Be("quiz");
        factory.GetHandler("quiz").Should().BeSameAs(first);
        factory.GetHandler("quiz").DefaultConfigJson.Should().Be("first");
    }
}