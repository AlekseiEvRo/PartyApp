using Microsoft.AspNetCore.Http;

namespace PartyApp.Api.Common.Files;

/// <summary>Определение типа изображения по сигнатуре файла — данным клиента не доверяем.</summary>
public static class ImageContent
{
    public static async Task<string?> DetectAsync(IFormFile file, CancellationToken ct)
    {
        byte[] header = new byte[12];

        await using Stream stream = file.OpenReadStream();
        int read = await stream.ReadAsync(header.AsMemory(0, header.Length), ct);

        if (read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return "image/jpeg";

        if (read >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
            return "image/png";

        if (read >= 12
            && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
            && header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P')
            return "image/webp";

        return null;
    }

    public static string ExtensionFor(string contentType) => contentType switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => ".jpg"
    };

    public static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg"
    };
}
