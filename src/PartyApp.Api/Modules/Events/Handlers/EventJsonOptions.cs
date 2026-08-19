using System.Text.Json;

namespace PartyApp.Api.Modules.Events.Handlers;

/// <summary>
/// Общие настройки JSON для всех обработчиков ивентов.
/// </summary>
public static class EventJsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}