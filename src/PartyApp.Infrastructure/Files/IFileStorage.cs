namespace PartyApp.Infrastructure.Files;

/// <summary>
/// Хранилище файлов на диске. Все пути относительные — от корня хранилища,
/// наружу (в БД и API) отдаются только такие пути.
/// </summary>
public interface IFileStorage
{
    /// <summary>Сохраняет поток и возвращает относительный путь файла.</summary>
    Task<string> SaveAsync(Stream content, string extension, CancellationToken ct = default);

    /// <summary>
    /// Сохраняет поток в указанную папку хранилища (например, avatars).
    /// Папка — только буквы, цифры, дефис и подчёркивание.
    /// </summary>
    Task<string> SaveAsync(Stream content, string extension, string folder, CancellationToken ct = default);

    /// <summary>Открывает файл на чтение или возвращает null, если его нет.</summary>
    Stream? OpenRead(string relativePath);

    bool Exists(string relativePath);

    /// <summary>Удаляет файл, если он есть. Ошибки удаления логируются, но не бросаются.</summary>
    void Delete(string relativePath);
}