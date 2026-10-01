using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;

namespace PartyApp.UnitTests.Testing;

/// <summary>
/// Фабрики сущностей для тестов: значения по умолчанию не важны для проверок,
/// важны только те, которые явно переданы.
/// </summary>
public static class TestData
{
    public static User User(string username = "player", UserRole role = UserRole.Player, string? displayName = null) => new()
    {
        Username = username,
        DisplayName = displayName ?? username,
        PasswordHash = "test-hash",
        Role = role
    };

    public static User UserWithWallet(
        string username = "player",
        int balance = 0,
        UserRole role = UserRole.Player)
    {
        User user = User(username, role);
        user.Wallet = new Wallet { UserId = user.Id, Balance = balance };
        return user;
    }

    public static EventDefinition Definition(
        string type,
        string configJson = "{}",
        bool isActive = true,
        string? displayName = null) => new()
    {
        Type = type,
        DisplayName = displayName ?? type,
        Description = null,
        ConfigJson = configJson,
        IsActive = isActive,
        Availability = AvailabilityMode.Manual
    };

    public static EventSession Session(
        EventDefinition definition,
        Guid startedById,
        EventSessionState state = EventSessionState.Active) => new()
    {
        Definition = definition,
        DefinitionId = definition.Id,
        StartedById = startedById,
        State = state,
        StartedAt = DateTime.UtcNow
    };

    public static QrToken QrToken(string code = "ABC234", int points = 20) => new()
    {
        Code = code,
        Points = points,
        CreatedAt = DateTime.UtcNow
    };
}