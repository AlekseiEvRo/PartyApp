using System.Security.Claims;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

using PartyApp.Api.Common.Security;
using PartyApp.Api.Hubs;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Admin;

public static class AdminEndpoints
{
    private const int MaxTransactionsPageSize = 100;
    private const int MaxAuditPageSize = 200;

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin")
            .WithTags("Admin")
            .RequireAuthorization("AdminOnly");

        // Список игроков с балансами
        group.MapGet("/players", async (AppDbContext db, CancellationToken ct) =>
        {
            var players = await db.Users
                .Include(u => u.Wallet)
                .OrderByDescending(u => u.Wallet != null ? u.Wallet.Balance : 0)
                .Select(u => new
                {
                    u.Id,
                    u.Username,
                    u.DisplayName,
                    Role = u.Role.ToString(),
                    u.IsActive,
                    u.ProfileUpdatedAt,
                    Balance = u.Wallet != null ? u.Wallet.Balance : 0,
                    u.CreatedAt
                })
                .ToListAsync(ct);

            return Results.Ok(players);
        });

        // Изменение имени, роли и блокировки игрока.
        // Смена роли/блокировка заодно отзывают все выданные игроку токены.
        group.MapPatch("/players/{playerId:guid}", async (
            Guid playerId,
            UpdatePlayerRequest request,
            ClaimsPrincipal admin,
            AppDbContext db,
            AdminAuditService audit,
            IHubContext<PartyHub> hub,
            CancellationToken ct) =>
        {
            User? player = await db.Users.SingleOrDefaultAsync(u => u.Id == playerId, ct);
            if (player is null)
                return Results.NotFound(new { error = "Игрок не найден" });

            if (request.DisplayName is null && request.Role is null && request.IsActive is null)
                return Results.BadRequest(new { error = "Нечего обновлять" });

            Guid adminId = GetUserId(admin);
            bool self = adminId == playerId;
            bool actorIsSuper = IsSuperAdmin(admin);

            string? newDisplayName = null;
            if (request.DisplayName is not null)
            {
                newDisplayName = request.DisplayName.Trim();
                if (newDisplayName.Length == 0)
                    return Results.BadRequest(new { error = "Имя не может быть пустым" });
                if (newDisplayName.Length > 50)
                    return Results.BadRequest(new { error = "Имя не длиннее 50 символов" });
            }

            UserRole? newRole = null;
            if (request.Role is not null)
            {
                string roleName = request.Role.Trim();
                bool knownRole = Enum.GetNames<UserRole>()
                    .Any(name => name.Equals(roleName, StringComparison.OrdinalIgnoreCase));

                if (!knownRole)
                    return Results.BadRequest(new { error = "Неизвестная роль" });

                newRole = Enum.Parse<UserRole>(roleName, ignoreCase: true);

                if (self && newRole != player.Role)
                    return Results.BadRequest(new { error = "Нельзя менять собственную роль" });

                // Роль супер-админа не может сменить никто
                if (player.Role == UserRole.SuperAdmin && newRole.Value != UserRole.SuperAdmin)
                    return Results.BadRequest(new { error = "Роль супер-админа изменить нельзя" });

                // Выдать роль супер-админа может только супер-админ
                if (newRole.Value == UserRole.SuperAdmin && player.Role != UserRole.SuperAdmin && !actorIsSuper)
                    return Results.Json(new { error = "Только супер-админ может назначить супер-админа" },
                        statusCode: StatusCodes.Status403Forbidden);
            }

            bool? newIsActive = request.IsActive;
            if (self && newIsActive == false)
                return Results.BadRequest(new { error = "Нельзя заблокировать себя" });

            // Прочие изменения супер-админа доступны только другому супер-админу
            bool changesSuperAdmin = player.Role == UserRole.SuperAdmin && !actorIsSuper && !self
                && (newDisplayName is not null || newIsActive.HasValue);

            if (changesSuperAdmin)
                return Results.Json(new { error = "Только супер-админ может менять супер-админа" },
                    statusCode: StatusCodes.Status403Forbidden);

            bool losesAdmin = IsPrivilegedRole(player.Role) && player.IsActive
                && ((newRole.HasValue && !IsPrivilegedRole(newRole.Value)) || newIsActive == false);

            if (losesAdmin && !await HasOtherActiveAdminAsync(db, playerId, ct))
                return Results.BadRequest(new { error = "Нельзя оставить систему без администратора" });

            bool revokeSessions = false;

            if (newDisplayName is not null && newDisplayName != player.DisplayName)
            {
                audit.Record(adminId, "rename", playerId, $"{player.DisplayName} → {newDisplayName}");
                player.DisplayName = newDisplayName;
            }

            if (newRole.HasValue && newRole.Value != player.Role)
            {
                audit.Record(adminId, "role_change", playerId, $"{player.Role} → {newRole.Value}");
                player.Role = newRole.Value;
                revokeSessions = true;
            }

            if (newIsActive.HasValue && newIsActive.Value != player.IsActive)
            {
                audit.Record(adminId, newIsActive.Value ? "unban" : "ban", playerId);
                player.IsActive = newIsActive.Value;
                revokeSessions = true;
            }

            if (revokeSessions)
                player.SecurityStamp = NewSecurityStamp();

            await db.SaveChangesAsync(ct);

            if (revokeSessions)
            {
                string reason = player.IsActive
                    ? "Роль изменена — войди заново"
                    : "Аккаунт заблокирован администратором";
                await NotifySessionRevokedAsync(hub, playerId, reason);
            }

            int balance = await db.Wallets.AsNoTracking()
                .Where(w => w.UserId == playerId)
                .Select(w => w.Balance)
                .SingleOrDefaultAsync(ct);

            return Results.Ok(new
            {
                player.Id,
                player.Username,
                player.DisplayName,
                Role = player.Role.ToString(),
                player.IsActive,
                Balance = balance
            });
        });

        // Кик: игрок остаётся активным, но все его токены перестают работать
        group.MapPost("/players/{playerId:guid}/kick", async (
            Guid playerId,
            ClaimsPrincipal admin,
            AppDbContext db,
            AdminAuditService audit,
            IHubContext<PartyHub> hub,
            CancellationToken ct) =>
        {
            User? player = await db.Users.SingleOrDefaultAsync(u => u.Id == playerId, ct);
            if (player is null)
                return Results.NotFound(new { error = "Игрок не найден" });

            Guid adminId = GetUserId(admin);
            if (adminId == playerId)
                return Results.BadRequest(new { error = "Нельзя кикнуть себя" });

            if (player.Role == UserRole.SuperAdmin && !IsSuperAdmin(admin))
                return Results.Json(new { error = "Только супер-админ может кикнуть супер-админа" },
                    statusCode: StatusCodes.Status403Forbidden);

            player.SecurityStamp = NewSecurityStamp();
            audit.Record(adminId, "kick", playerId);
            await db.SaveChangesAsync(ct);

            await NotifySessionRevokedAsync(hub, playerId, "Сессия завершена администратором");
            return Results.Ok(new { success = true });
        });

        // Сброс пароля: админ получает новый пароль и передаёт его игроку.
        // Старый пароль и все выданные токены сразу перестают работать.
        group.MapPost("/players/{playerId:guid}/reset-password", async (
            Guid playerId,
            ClaimsPrincipal admin,
            AppDbContext db,
            AdminAuditService audit,
            IPasswordHasher<User> passwordHasher,
            IHubContext<PartyHub> hub,
            CancellationToken ct) =>
        {
            User? player = await db.Users.SingleOrDefaultAsync(u => u.Id == playerId, ct);
            if (player is null)
                return Results.NotFound(new { error = "Игрок не найден" });

            Guid adminId = GetUserId(admin);
            if (adminId == playerId)
                return Results.BadRequest(new { error = "Нельзя сбросить пароль себе" });

            if (player.Role == UserRole.SuperAdmin && !IsSuperAdmin(admin))
                return Results.Json(new { error = "Только супер-админ может сбросить пароль супер-админа" },
                    statusCode: StatusCodes.Status403Forbidden);

            string password = PasswordGenerator.Generate();
            player.PasswordHash = passwordHasher.HashPassword(player, password);
            player.SecurityStamp = NewSecurityStamp();

            audit.Record(adminId, "password_reset", playerId);
            await db.SaveChangesAsync(ct);

            await NotifySessionRevokedAsync(hub, playerId, "Пароль изменён администратором");
            return Results.Ok(new { password });
        });

        // Журнал действий администраторов
        group.MapGet("/audit", async (
            AppDbContext db,
            int? limit,
            int? offset,
            CancellationToken ct) =>
        {
            int take = Math.Clamp(limit ?? 50, 1, MaxAuditPageSize);
            int skip = Math.Max(offset ?? 0, 0);

            var query = db.AdminAuditLogs.AsNoTracking();

            int total = await query.CountAsync(ct);
            var items = await query
                .OrderByDescending(a => a.CreatedAt)
                .Skip(skip)
                .Take(take)
                .Select(a => new
                {
                    a.Id,
                    AdminName = a.Admin.DisplayName,
                    a.TargetUserId,
                    TargetName = a.TargetUser != null ? a.TargetUser.DisplayName : null,
                    a.Action,
                    a.Details,
                    a.CreatedAt
                })
                .ToListAsync(ct);

            return Results.Ok(new { items, total });
        });

        // История транзакций конкретного игрока (новые сверху)
        group.MapGet("/players/{playerId:guid}/transactions", async (
            Guid playerId,
            AppDbContext db,
            int? limit,
            int? offset,
            CancellationToken ct) =>
        {
            var player = await db.Users.AsNoTracking()
                .Where(u => u.Id == playerId)
                .Select(u => new
                {
                    u.Id,
                    u.Username,
                    u.DisplayName
                })
                .SingleOrDefaultAsync(ct);

            if (player is null)
                return Results.NotFound(new { error = "Игрок не найден" });

            var wallet = await db.Wallets.AsNoTracking()
                .SingleOrDefaultAsync(w => w.UserId == playerId, ct);

            if (wallet is null)
                return Results.Ok(new { player, items = Array.Empty<object>(), total = 0 });

            var take = Math.Clamp(limit ?? 50, 1, MaxTransactionsPageSize);
            var skip = Math.Max(offset ?? 0, 0);

            var query = db.WalletTransactions.AsNoTracking()
                .Where(t => t.WalletId == wallet.Id);

            var total = await query.CountAsync(ct);
            var items = await query
                .OrderByDescending(t => t.CreatedAt)
                .Skip(skip)
                .Take(take)
                .Select(t => new
                {
                    t.Id,
                    t.Amount,
                    Type = t.Type.ToString(),
                    t.Description,
                    t.RelatedSessionId,
                    t.CreatedAt
                })
                .ToListAsync(ct);

            return Results.Ok(new { player, items, total });
        });

        // Лидерборд: только игроки — админы и супер-админы не участвуют в рейтинге
        group.MapGet("/leaderboard", async (AppDbContext db, CancellationToken ct) =>
        {
            var leaderboard = await db.Users
                .Include(u => u.Wallet)
                .Where(u => u.Role == UserRole.Player)
                .OrderByDescending(u => u.Wallet != null ? u.Wallet.Balance : 0)
                .Take(20)
                .Select(u => new
                {
                    u.Id,
                    u.DisplayName,
                    u.ProfileUpdatedAt,
                    Balance = u.Wallet != null ? u.Wallet.Balance : 0
                })
                .ToListAsync(ct);

            return Results.Ok(leaderboard);
        });

        // Ручное начисление/списание баллов
        group.MapPost("/grant-points", async (
            GrantPointsRequest request,
            ClaimsPrincipal admin,
            AppDbContext db,
            IPointsAwardService pointsAward,
            AdminAuditService audit,
            CancellationToken ct) =>
        {
            var playerId = request.PlayerId;
            var amount = request.Amount;
            var reason = string.IsNullOrWhiteSpace(request.Reason) ? "Ручное начисление" : request.Reason;

            if (amount == 0)
                return Results.BadRequest(new { error = "Сумма не может быть 0" });

            var wallet = await db.Wallets.SingleOrDefaultAsync(w => w.UserId == playerId, ct);
            if (wallet is null)
                return Results.NotFound(new { error = "Кошелёк игрока не найден" });

            if (amount < 0 && wallet.Balance + amount < 0)
                return Results.BadRequest(new { error = "Недостаточно баллов для списания у игрока" });

            var transactionType = amount > 0
                ? WalletTransactionType.AdminGrant
                : WalletTransactionType.AdminDeduct;

            var newBalance = await pointsAward.AwardAsync(playerId, amount, reason, transactionType, ct: ct);

            audit.Record(
                GetUserId(admin),
                amount > 0 ? "grant_points" : "deduct_points",
                playerId,
                $"{amount:+#;-#;0} · {reason}");
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                success = true,
                playerId,
                amount,
                newBalance,
                reason
            });
        });

        // Список сессий ивентов
        group.MapGet("/sessions", async (AppDbContext db, CancellationToken ct) =>
        {
            var sessions = await db.EventSessions
                .Include(s => s.Definition)
                .Include(s => s.StartedBy)
                .OrderByDescending(s => s.StartedAt)
                .Select(s => new
                {
                    s.Id,
                    s.DefinitionId,
                    DefinitionName = s.Definition.DisplayName,
                    Type = s.Definition.Type,
                    State = s.State.ToString(),
                    StartedAt = s.StartedAt,
                    EndsAt = s.EndsAt,
                    EndedAt = s.EndedAt,
                    StartedBy = s.StartedBy.DisplayName,
                    SubmissionCount = s.Submissions.Count
                })
                .ToListAsync(ct);

            return Results.Ok(sessions);
        });

        return app;
    }

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        return Guid.Parse(user.FindFirst("sub")!.Value);
    }

    private static string NewSecurityStamp()
    {
        return Guid.NewGuid().ToString("N");
    }

    private static bool IsSuperAdmin(ClaimsPrincipal user)
    {
        return user.IsInRole(nameof(UserRole.SuperAdmin));
    }

    /// <summary>Админ или супер-админ — роль с доступом к админке.</summary>
    private static bool IsPrivilegedRole(UserRole role)
    {
        return role is UserRole.Admin or UserRole.SuperAdmin;
    }

    private static Task<bool> HasOtherActiveAdminAsync(AppDbContext db, Guid excludeUserId, CancellationToken ct)
    {
        return db.Users.AnyAsync(
            u => u.Id != excludeUserId
                 && (u.Role == UserRole.Admin || u.Role == UserRole.SuperAdmin)
                 && u.IsActive,
            ct);
    }

    /// <summary>Сообщает клиенту игрока, что его сессия отозвана (кик, бан, смена роли).</summary>
    private static Task NotifySessionRevokedAsync(IHubContext<PartyHub> hub, Guid playerId, string reason)
    {
        return hub.Clients.User(playerId.ToString()).SendAsync("SessionRevoked", new { reason });
    }
}

public record GrantPointsRequest(Guid PlayerId, int Amount, string? Reason);

public record UpdatePlayerRequest(string? DisplayName, string? Role, bool? IsActive);
