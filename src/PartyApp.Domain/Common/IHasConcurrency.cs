namespace PartyApp.Domain.Common;

public interface IHasConcurrency
{
    int Version { get; set; }
}