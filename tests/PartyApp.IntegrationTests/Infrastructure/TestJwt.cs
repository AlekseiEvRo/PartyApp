namespace PartyApp.IntegrationTests.Infrastructure;

/// <summary>Параметры подписи JWT, совпадающие с конфигом тестового приложения.</summary>
public static class TestJwt
{
    public const string SigningKey = "IntegrationTestSigningKeyThatIsLongEnough_42!";
    public const string Issuer = "party-app-tests";
    public const string Audience = "party-app-tests-clients";
}