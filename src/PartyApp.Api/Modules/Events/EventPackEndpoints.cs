using System.Security.Claims;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using PartyApp.Api.Modules.Events.Services;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.Infrastructure.Persistence;

namespace PartyApp.Api.Modules.Events;

/// <summary>
/// Пакеты контента: выгрузка и загрузка определений ивентов (вопросы квиза,
/// песни, фанты и т.д.) одним JSON-файлом — удобно готовить вечеринку заранее.
/// </summary>
public static class EventPackEndpoints
{
    private const int MaxDefinitions = 200;

    public static IEndpointRouteBuilder MapEventPackEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/events")
            .WithTags("Admin")
            .RequireAuthorization("AdminOnly");

        group.MapGet("/definitions/export", async (AppDbContext db, CancellationToken ct) =>
        {
            List<EventPackDefinition> definitions = await db.EventDefinitions.AsNoTracking()
                .OrderBy(d => d.CreatedAt)
                .Select(d => new EventPackDefinition(
                    d.Type,
                    d.DisplayName,
                    d.Description,
                    d.ConfigJson,
                    d.DurationMinutes))
                .ToListAsync(ct);

            var pack = new EventPack(1, DateTime.UtcNow, definitions);
            string json = JsonSerializer.Serialize(
                pack,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

            string fileName = $"party-events-{DateTime.UtcNow:yyyyMMdd}.json";
            return Results.File(Encoding.UTF8.GetBytes(json), "application/json", fileName);
        });

        group.MapPost("/definitions/import", async (
            EventPack pack,
            ClaimsPrincipal user,
            AppDbContext db,
            IEventHandlerFactory handlerFactory,
            CancellationToken ct) =>
        {
            if (pack.Definitions is null || pack.Definitions.Count == 0)
                return Results.BadRequest(new { error = "В пакете нет определений" });

            if (pack.Definitions.Count > MaxDefinitions)
                return Results.BadRequest(new { error = $"В пакете больше {MaxDefinitions} определений" });

            Guid adminId = Guid.Parse(user.FindFirst("sub")!.Value);

            HashSet<string> knownNames = new(
                await db.EventDefinitions.AsNoTracking().Select(d => d.DisplayName).ToListAsync(ct),
                StringComparer.OrdinalIgnoreCase);

            List<EventDefinition> created = new();
            List<object> errors = new();
            int skipped = 0;

            foreach (EventPackDefinition item in pack.Definitions)
            {
                string type = item.Type?.Trim() ?? string.Empty;
                string displayName = item.DisplayName?.Trim() ?? string.Empty;

                if (!handlerFactory.HasHandler(type))
                {
                    errors.Add(new { displayName, error = $"Неизвестный тип ивента: {type}" });
                    continue;
                }

                if (displayName.Length == 0)
                {
                    errors.Add(new { displayName, error = "Название не может быть пустым" });
                    continue;
                }

                if (displayName.Length > 100)
                {
                    errors.Add(new { displayName, error = "Название длиннее 100 символов" });
                    continue;
                }

                if (item.DurationMinutes is < 1 or > 1440)
                {
                    errors.Add(new { displayName, error = "Длительность — от 1 до 1440 минут" });
                    continue;
                }

                string configJson = string.IsNullOrWhiteSpace(item.ConfigJson) ? "{}" : item.ConfigJson;

                try
                {
                    using JsonDocument document = JsonDocument.Parse(configJson);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        errors.Add(new { displayName, error = "ConfigJson должен быть JSON-объектом" });
                        continue;
                    }
                }
                catch (JsonException)
                {
                    errors.Add(new { displayName, error = "ConfigJson не является валидным JSON" });
                    continue;
                }

                // Совпадение по названию считаем уже импортированным — не плодим дубли
                if (!knownNames.Add(displayName))
                {
                    skipped++;
                    continue;
                }

                created.Add(new EventDefinition
                {
                    Type = type,
                    DisplayName = displayName,
                    Description = string.IsNullOrWhiteSpace(item.Description) ? null : item.Description.Trim(),
                    ConfigJson = configJson,
                    Availability = AvailabilityMode.Manual,
                    DurationMinutes = item.DurationMinutes,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    CreatedById = adminId
                });
            }

            if (created.Count > 0)
            {
                db.EventDefinitions.AddRange(created);
                await db.SaveChangesAsync(ct);
            }

            return Results.Ok(new { added = created.Count, skipped, errors });
        });

        return app;
    }
}

public record EventPack(int? Version, DateTime? ExportedAt, List<EventPackDefinition>? Definitions);

public record EventPackDefinition(
    string? Type,
    string? DisplayName,
    string? Description,
    string? ConfigJson,
    int? DurationMinutes);
