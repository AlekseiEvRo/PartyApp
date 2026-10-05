using System.Security.Claims;

using PartyApp.Domain.Enums;

namespace PartyApp.Api.Common.Security;

/// <summary>
/// Единая проверка админских ролей для обработчиков, у которых нет политики AdminOnly.
/// Политика пускает и Admin, и SuperAdmin, поэтому проверки внутри эндпоинтов
/// обязаны учитывать обе роли — иначе супер-админ видит только игровые данные.
/// </summary>
public static class ClaimsPrincipalExtensions
{
    public static bool IsAdminOrSuperAdmin(this ClaimsPrincipal user)
    {
        return user.IsInRole(nameof(UserRole.Admin)) || user.IsInRole(nameof(UserRole.SuperAdmin));
    }
}