namespace PartyApp.Domain.Enums;

public enum UserRole
{
    Player = 0,
    Admin = 10,
    /// <summary>Супер-админ: его роль нельзя изменить никому, остальные действия — только другому супер-админу.</summary>
    SuperAdmin = 100
}