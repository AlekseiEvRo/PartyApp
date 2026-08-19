namespace PartyApp.Api.Modules.Toast;

public record ToastResult(
    bool Success,
    DateTime? BusyUntilUtc,
    string? BusyByName,
    int Points,
    string? Message);

public record ToastStatus(
    bool IsBusy,
    DateTime? BusyUntilUtc,
    string? CurrentSpeakerName);