using PartyApp.Domain.Common;
using PartyApp.Domain.Enums;

namespace PartyApp.Domain.Entities;

public class User : BaseEntity
{
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Player;

    /// <summary>Заблокированные не могут войти, а выданные им токены отклоняются.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Отметка безопасности в JWT: смена роли, пароля или кик меняют её,
    /// из-за чего все ранее выданные токены перестают работать.
    /// </summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Относительный путь к загруженному аватару. Пусто — показываем инициалы.</summary>
    public string? AvatarPath { get; set; }

    /// <summary>Короткий статус-эмодзи рядом с именем (например, «🎂»).</summary>
    public string? StatusEmoji { get; set; }

    /// <summary>Версия профиля: меняется при обновлении аватара или статуса — сбрасывает кэш у клиентов.</summary>
    public DateTime? ProfileUpdatedAt { get; set; }

    public Wallet Wallet { get; set; } = null!;
}