namespace PartyApp.Api.Modules.Achievements;

/// <summary>Описание достижения. Каталог живёт в коде и не редактируется из админки.</summary>
public record AchievementDefinition(
    string Code,
    string Title,
    string Icon,
    string Description,
    int Points);

public static class AchievementCatalog
{
    public const string FirstAnswer = "first_answer";
    public const string QuizWinner = "quiz_winner";
    public const string Photographer = "photographer";
    public const string PhotoLoved = "photo_loved";
    public const string QrHunter = "qr_hunter";
    public const string DareStar = "dare_star";
    public const string BingoLine = "bingo_line";
    public const string Rich100 = "rich_100";

    public static readonly IReadOnlyList<AchievementDefinition> All = new List<AchievementDefinition>
    {
        new(FirstAnswer, "Первый шаг", "🚀", "Ответить на первый ивент", 5),
        new(QuizWinner, "Победитель квиза", "🏆", "Набрать больше всех баллов в квизе", 15),
        new(Photographer, "Фотограф", "📸", "Получить 5 одобренных фото", 10),
        new(PhotoLoved, "Любимое фото", "❤️", "Собрать 5 лайков на одном фото", 10),
        new(QrHunter, "Охотник за QR", "📷", "Активировать 5 QR-кодов", 10),
        new(DareStar, "Душа компании", "🎭", "Выполнить 3 фанта", 10),
        new(BingoLine, "Линия бинго", "🎯", "Собрать линию в бинго", 10),
        new(Rich100, "Сотня", "💰", "Заработать 100 баллов за ивенты, QR, фото и достижения", 10)
    };

    public static AchievementDefinition Get(string code)
    {
        return All.Single(a => a.Code == code);
    }
}
