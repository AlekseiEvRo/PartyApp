using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using PartyApp.Api.Hubs;
using PartyApp.Api.Modules.Achievements;
using PartyApp.Api.Modules.Wallet;

namespace PartyApp.UnitTests.Testing;

/// <summary>
/// Готовый AchievementService для тестов: начисление подменяется отдельным
/// substitute, чтобы выдача достижений не попадала в проверки других сервисов.
/// </summary>
public static class TestAchievements
{
    public static AchievementService Create(
        IServiceScopeFactory scopeFactory,
        IPointsAwardService? pointsAward = null,
        IHubContext<PartyHub>? hub = null)
    {
        return new AchievementService(
            scopeFactory,
            pointsAward ?? Substitute.For<IPointsAwardService>(),
            hub ?? new RecordingHubContext(),
            new FakePushNotificationService(),
            NullLogger<AchievementService>.Instance);
    }
}
