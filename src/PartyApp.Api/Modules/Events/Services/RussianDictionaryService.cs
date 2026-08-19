using System.Text;

namespace PartyApp.Api.Modules.Events.Services;

/// <summary>
/// Сервис проверки существования слова в русском языке.
/// Загружает словарь из файла в HashSet для быстрой проверки.
/// </summary>
public class RussianDictionaryService
{
    private readonly HashSet<string> _words;
    private readonly ILogger<RussianDictionaryService> _logger;
    private readonly bool _isLoaded;

    public RussianDictionaryService(IConfiguration configuration, ILogger<RussianDictionaryService> logger)
    {
        _logger = logger;
        _words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var dictionaryPath = configuration["Dictionary:FilePath"] ?? "App_Data/russian_words.txt";

        if (File.Exists(dictionaryPath))
        {
            var lines = File.ReadAllLines(dictionaryPath, Encoding.UTF8);
            foreach (var line in lines)
            {
                var word = NormalizeWord(line);
                if (!string.IsNullOrWhiteSpace(word))
                {
                    _words.Add(word);
                }
            }
            _isLoaded = true;
            _logger.LogInformation("Loaded {Count} words from dictionary: {Path}", _words.Count, dictionaryPath);
        }
        else
        {
            _isLoaded = false;
            _logger.LogWarning(
                "Dictionary file not found at {Path}. Word existence check will be skipped.",
                dictionaryPath);
        }
    }

    /// <summary>
    /// Проверяет, существует ли слово в словаре.
    /// Если словарь не загружен, возвращает true (пропускает проверку).
    /// </summary>
    public bool WordExists(string word)
    {
        if (!_isLoaded)
            return false; // Если словаря нет, пропускаем проверку

        var normalized = NormalizeWord(word);
        return _words.Contains(normalized);
    }

    /// <summary>
    /// Нормализует слово: нижний регистр, Ё → Е, убираем пробелы.
    /// </summary>
    private static string NormalizeWord(string word)
    {
        return word
            .Trim()
            .ToLowerInvariant()
            .Replace('ё', 'е');
    }
}