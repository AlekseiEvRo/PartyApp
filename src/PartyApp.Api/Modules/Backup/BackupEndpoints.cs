namespace PartyApp.Api.Modules.Backup;

public static class BackupEndpoints
{
    public static IEndpointRouteBuilder MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin")
            .WithTags("Admin")
            .RequireAuthorization("AdminOnly");

        // Список локальных резервных копий
        group.MapGet("/backups", (BackupService backups) => Results.Ok(backups.List()));

        // Создать свежий бэкап и сразу отдать его файлом.
        // Отдаём через поток: Results.File с относительным путём ищет файл в wwwroot.
        group.MapGet("/backup", async (BackupService backups, CancellationToken ct) =>
        {
            string path = await backups.CreateAsync(ct);
            FileStream stream = File.OpenRead(path);
            return Results.Stream(stream, "application/zip", Path.GetFileName(path));
        });

        return app;
    }
}
