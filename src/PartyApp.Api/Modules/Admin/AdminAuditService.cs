using PartyApp.Domain.Entities;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Admin;

/// <summary>
/// Пишет действия администраторов в общий журнал. Запись добавляется в тот же
/// AppDbContext, что и само действие, и сохраняется вместе с ним одним SaveChanges.
/// </summary>
public class AdminAuditService
{
    private readonly AppDbContext _db;

    public AdminAuditService(AppDbContext db)
    {
        _db = db;
    }

    public void Record(Guid adminId, string action, Guid? targetUserId = null, string? details = null)
    {
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            AdminId = adminId,
            TargetUserId = targetUserId,
            Action = action,
            Details = details
        });
    }
}
