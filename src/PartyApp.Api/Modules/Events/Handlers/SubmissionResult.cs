namespace PartyApp.Api.Modules.Events.Handlers;

/// <summary>
/// Результат обработки действия игрока.
/// </summary>
public record SubmissionResult(
    bool Success,
    int PointsAwarded,
    string? Message,
    object? Data = null,
    int? DurationMs = null,
    int? TicketNumber = null)
{
    public static SubmissionResult Ok(
        int points,
        string? message = null,
        object? data = null,
        int? durationMs = null,
        int? ticketNumber = null)
        => new(true, points, message, data, durationMs, ticketNumber);

    public static SubmissionResult Fail(string message, object? data = null)
        => new(false, 0, message, data);
}