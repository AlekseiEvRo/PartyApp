namespace PartyApp.Api.Modules.Screen;

/// <summary>Режимы большого экрана.</summary>
public static class ScreenModes
{
    public const string Idle = "idle";
    public const string Leaderboard = "leaderboard";
    public const string Event = "event";
    public const string Photos = "photos";
    public const string Message = "message";
    public const string Shop = "shop";
    public const string Rotation = "rotation";
    public const string Lots = "lots";
    public const string Qr = "qr";
    public const string Spy = "spy";
    public const string Summary = "summary";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Idle, Leaderboard, Event, Photos, Message, Shop, Rotation, Lots, Qr, Spy, Summary
    };
}

public record ScreenStateDto(
    string Mode,
    Guid? SessionId,
    string? Message,
    int Version,
    DateTime ServerTimeUtc);

public record ConfettiDto(int Version, DateTime ServerTimeUtc);

/// <summary>
/// Состояние большого экрана для проектора. Живёт в памяти singleton-ом:
/// после перезапуска API экран возвращается в режим ожидания.
/// </summary>
public class ScreenService
{
    private readonly object _lock = new();
    private ScreenStateDto _state = new(ScreenModes.Idle, null, null, 0, DateTime.UtcNow);
    private int _confettiVersion;

    public ScreenStateDto GetState()
    {
        lock (_lock)
        {
            return _state with { ServerTimeUtc = DateTime.UtcNow };
        }
    }

    public ScreenStateDto SetState(string mode, Guid? sessionId = null, string? message = null)
    {
        if (!ScreenModes.All.Contains(mode))
            throw new ArgumentException($"Неизвестный режим экрана: {mode}", nameof(mode));

        lock (_lock)
        {
            _state = new ScreenStateDto(
                mode.ToLowerInvariant(),
                sessionId,
                message,
                _state.Version + 1,
                DateTime.UtcNow);

            return _state;
        }
    }

    public ConfettiDto FireConfetti()
    {
        lock (_lock)
        {
            _confettiVersion++;
            return new ConfettiDto(_confettiVersion, DateTime.UtcNow);
        }
    }
}