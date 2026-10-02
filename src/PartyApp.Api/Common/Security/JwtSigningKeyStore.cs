using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PartyApp.Api.Common.Security;

/// <summary>
/// Готовит ключ подписи JWT по порядку: конфиг → файл App_Data/jwt.json → генерация.
/// Сгенерированный ключ сохраняется, иначе после перезапуска все выданные токены
/// станут недействительными, а на проде мог бы использоваться общеизвестный dev-ключ.
/// </summary>
public static class JwtSigningKeyStore
{
    private const int KeySizeBytes = 64;
    private const int MinimumKeyBytes = 32;

    public static JwtSigningKey Resolve(IConfiguration configuration)
    {
        string? configured = configuration["Jwt:SigningKey"];
        if (!string.IsNullOrWhiteSpace(configured))
            return new JwtSigningKey(Validate(configured, "конфига Jwt:SigningKey"), "конфиг");

        string path = configuration["Jwt:KeysFile"] ?? "App_Data/jwt.json";

        if (File.Exists(path))
        {
            string? stored = ReadStoredKey(path);
            if (!string.IsNullOrWhiteSpace(stored))
                return new JwtSigningKey(Validate(stored, $"файла {path}"), $"файл {path}");
        }

        string generated = Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeySizeBytes));
        WriteStoredKey(path, generated);
        return new JwtSigningKey(generated, $"файл {path} (сгенерирован)");
    }

    private static string Validate(string key, string source)
    {
        if (Encoding.UTF8.GetByteCount(key) < MinimumKeyBytes)
            throw new InvalidOperationException(
                $"Ключ подписи JWT из {source} слишком короткий: нужно минимум {MinimumKeyBytes} байт");

        return key;
    }

    private static string? ReadStoredKey(string path)
    {
        try
        {
            StoredSigningKey? stored = JsonSerializer.Deserialize<StoredSigningKey>(File.ReadAllText(path));
            return stored?.SigningKey;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Не удалось прочитать ключ подписи JWT из {path}. Исправь файл или удали его, чтобы ключ сгенерировался заново",
                ex);
        }
    }

    private static void WriteStoredKey(string path, string key)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(
                path,
                JsonSerializer.Serialize(
                    new StoredSigningKey { SigningKey = key },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Не удалось сохранить ключ подписи JWT в {path}", ex);
        }
    }

    private sealed class StoredSigningKey
    {
        public string SigningKey { get; set; } = string.Empty;
    }
}

/// <summary>Ключ подписи JWT и понятное человеку описание источника (для лога при старте).</summary>
public record JwtSigningKey(string Key, string Source);
