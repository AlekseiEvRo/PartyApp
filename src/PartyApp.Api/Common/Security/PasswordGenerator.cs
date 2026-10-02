using System.Security.Cryptography;

namespace PartyApp.Api.Common.Security;

/// <summary>
/// Генерирует читаемый пароль без похожих символов (0/O, 1/l/I),
/// чтобы его можно было продиктовать вслух и легко ввести с телефона.
/// </summary>
public static class PasswordGenerator
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    public static string Generate(int length = 10)
    {
        if (length < 6)
            throw new ArgumentOutOfRangeException(nameof(length), "Пароль короче 6 символов не принимается");

        char[] chars = new char[length];
        for (int i = 0; i < length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];

        return new string(chars);
    }
}
