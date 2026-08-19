namespace PartyApp.Api.Modules.Toast;

public class ToastState
{
    public Guid? CurrentSpeakerId { get; set; }
    public string? CurrentSpeakerName { get; set; }
    public DateTime? BusyUntilUtc { get; set; }

    public bool IsBusy(DateTime nowUtc) =>
        BusyUntilUtc.HasValue && nowUtc < BusyUntilUtc.Value;
}