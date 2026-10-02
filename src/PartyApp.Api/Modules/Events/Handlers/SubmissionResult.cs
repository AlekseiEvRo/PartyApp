namespace PartyApp.Api.Modules.Events.Handlers;

/// <summary>
/// Результат обработки действия игрока.
/// </summary>
public record SubmissionResult(
    bool Success,
    int PointsAwarded,
    string? Message,
    object? Data = null,
    int? DurationMs = null)
{
    public static SubmissionResult Ok(int points, string? message = null, object? data = null, int? durationMs = null)
        => new(true, points, message, data, durationMs);

    public static SubmissionResult Fail(string message, object? data = null)
        => new(false, 0, message, data);
}