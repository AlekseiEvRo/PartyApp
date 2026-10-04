using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using PartyApp.Api.Modules.Achievements;
using PartyApp.Api.Modules.Wallet;
using PartyApp.Domain.Entities;
using PartyApp.Domain.Enums;
using PartyApp.UnitTests.Testing;

namespace PartyApp.UnitTests.Achievements;

public class AchievementServiceTests : IDisposable
{
    private readonly SqliteTestHost _host;
    private readonly IPointsAwardService _award = Substitute.For<IPointsAwardService>();
    private readonly RecordingHubContext _hub = new();
    private readonly FakePushNotificationService _push = new();
    private readonly AchievementService _service;

    public AchievementServiceTests()
    {
        _host = new SqliteTestHost();
        _service = new AchievementService(
            _host.ScopeFactory,
            _award,
            _hub,
            _push,
            NullLogger<AchievementService>.Instance);
    }

    public void Dispose()
    {
        _host.Dispose();
    }

    private async Task<User> SeedUserAsync(int balance = 0)
    {
        User user = TestData.UserWithWallet($"u-{Guid.NewGuid():N}"[..10], balance);
        _host.Db.Users.Add(user);
        await _host.Db.SaveChangesAsync();
        return user;
    }

    private async Task<(EventDefinition Definition, EventSession Session)> SeedSessionAsync(
        string type = "quiz",
        string configJson = "{}")
    {
        User admin = TestData.User($"admin-{Guid.NewGuid():N}"[..10], UserRole.Admin);
        EventDefinition definition = TestData.Definition(type, configJson);
        EventSession session = TestData.Session(definition, admin.Id);

        _host.Db.Users.Add(admin);
        _host.Db.EventDefinitions.Add(definition);
        _host.Db.EventSessions.Add(session);
        await _host.Db.SaveChangesAsync();

        return (definition, session);
    }

    private async Task<PlayerSubmission> SeedSubmissionAsync(Guid sessionId, Guid playerId, int score)
    {
        PlayerSubmission submission = new()
        {
            SessionId = sessionId,
            PlayerId = playerId,
            Score = score,
            SubmittedAt = DateTime.UtcNow
        };

        _host.Db.PlayerSubmissions.Add(submission);
        await _host.Db.SaveChangesAsync();
        return submission;
    }

    private async Task SeedPhotoAsync(Guid authorId, ModerationStatus status = ModerationStatus.Approved)
    {
        _host.Db.PartyPhotos.Add(new PartyPhoto
        {
            UploadedById = authorId,
            StoragePath = $"photos/{Guid.NewGuid():N}.jpg",
            OriginalFileName = "photo.jpg",
            ContentType = "image/jpeg",
            Status = status
        });

        await _host.Db.SaveChangesAsync();
    }

    private async Task<Guid> SeedLikedPhotoAsync(Guid authorId, int likes)
    {
        PartyPhoto photo = new()
        {
            UploadedById = authorId,
            StoragePath = $"photos/{Guid.NewGuid():N}.jpg",
            OriginalFileName = "photo.jpg",
            ContentType = "image/jpeg",
            Status = ModerationStatus.Approved
        };

        _host.Db.PartyPhotos.Add(photo);

        for (int i = 0; i < likes; i++)
        {
            User liker = TestData.User($"liker-{i}-{Guid.NewGuid():N}"[..12]);
            _host.Db.Users.Add(liker);
            _host.Db.PhotoLikes.Add(new PhotoLike { PhotoId = photo.Id, UserId = liker.Id });
        }

        await _host.Db.SaveChangesAsync();
        return photo.Id;
    }

    private async Task<bool> HasAchievementAsync(Guid userId, string code)
    {
        return await _host.DbAsync(db => db.UserAchievements
            .AsNoTracking()
            .AnyAsync(a => a.UserId == userId && a.Code == code));
    }

    [Fact]
    public async Task EvaluatePlayerAsync_FirstSubmission_UnlocksFirstAnswer()
    {
        User player = await SeedUserAsync();
        (_, EventSession session) = await SeedSessionAsync();
        await SeedSubmissionAsync(session.Id, player.Id, score: 10);

        await _service.EvaluatePlayerAsync(player.Id);

        (await HasAchievementAsync(player.Id, AchievementCatalog.FirstAnswer)).Should().BeTrue();

        await _award.Received(1).AwardAsync(
            player.Id,
            5,
            "Достижение: Первый шаг",
            WalletTransactionType.Achievement,
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());

        _hub.SingleCall("AchievementUnlocked").Target.Should().Be($"user:{player.Id}");
        _push.Calls.Should().Contain(call => call.UserIds != null && call.UserIds.Contains(player.Id));
    }

    [Fact]
    public async Task EvaluatePlayerAsync_DoesNotAwardTwice()
    {
        User player = await SeedUserAsync();
        (_, EventSession session) = await SeedSessionAsync();
        await SeedSubmissionAsync(session.Id, player.Id, score: 10);

        await _service.EvaluatePlayerAsync(player.Id);
        await _service.EvaluatePlayerAsync(player.Id);

        int count = await _host.DbAsync(db => db.UserAchievements
            .AsNoTracking()
            .CountAsync(a => a.UserId == player.Id && a.Code == AchievementCatalog.FirstAnswer));

        count.Should().Be(1);
        await _award.Received(1).AwardAsync(
            player.Id,
            Arg.Any<int>(),
            Arg.Any<string>(),
            Arg.Any<WalletTransactionType>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluatePlayerAsync_FiveApprovedPhotos_UnlocksPhotographer()
    {
        User player = await SeedUserAsync();
        for (int i = 0; i < 5; i++)
            await SeedPhotoAsync(player.Id);

        await SeedPhotoAsync(player.Id, ModerationStatus.Pending);

        await _service.EvaluatePlayerAsync(player.Id);

        (await HasAchievementAsync(player.Id, AchievementCatalog.Photographer)).Should().BeTrue();
    }

    [Fact]
    public async Task EvaluatePlayerAsync_FiveQrCodes_UnlocksQrHunter()
    {
        User player = await SeedUserAsync();

        for (int i = 0; i < 5; i++)
        {
            _host.Db.QrTokens.Add(new QrToken
            {
                Code = $"CODE{i}{Guid.NewGuid():N}"[..8],
                Points = 10,
                RedeemedById = player.Id,
                RedeemedAt = DateTime.UtcNow
            });
        }

        await _host.Db.SaveChangesAsync();

        await _service.EvaluatePlayerAsync(player.Id);

        (await HasAchievementAsync(player.Id, AchievementCatalog.QrHunter)).Should().BeTrue();
    }

    [Fact]
    public async Task EvaluatePlayerAsync_ThreeConfirmedDares_UnlocksDareStar()
    {
        User player = await SeedUserAsync();

        // Уникальный индекс — один фант на игрока за сессию, поэтому три разные сессии
        for (int i = 0; i < 3; i++)
        {
            (_, EventSession session) = await SeedSessionAsync("dare");

            _host.Db.DareAssignments.Add(new DareAssignment
            {
                SessionId = session.Id,
                PlayerId = player.Id,
                TaskIndex = i,
                Task = $"Задание {i}",
                Status = DareStatus.Confirmed,
                ConfirmedAt = DateTime.UtcNow
            });

            await _host.Db.SaveChangesAsync();
        }

        await _service.EvaluatePlayerAsync(player.Id);

        (await HasAchievementAsync(player.Id, AchievementCatalog.DareStar)).Should().BeTrue();
    }

    [Fact]
    public async Task EvaluatePlayerAsync_EventRewardsReachHundred_UnlocksRich()
    {
        User player = await SeedUserAsync(balance: 100);

        _host.Db.WalletTransactions.Add(new WalletTransaction
        {
            WalletId = player.Wallet.Id,
            Amount = 100,
            Type = WalletTransactionType.EventReward,
            CreatedAt = DateTime.UtcNow
        });
        await _host.Db.SaveChangesAsync();

        await _service.EvaluatePlayerAsync(player.Id);

        (await HasAchievementAsync(player.Id, AchievementCatalog.Rich100)).Should().BeTrue();
    }

    [Fact]
    public async Task EvaluatePlayerAsync_AdminGrantOnly_DoesNotUnlockRich()
    {
        User player = await SeedUserAsync(balance: 100);

        _host.Db.WalletTransactions.Add(new WalletTransaction
        {
            WalletId = player.Wallet.Id,
            Amount = 100,
            Type = WalletTransactionType.AdminGrant,
            CreatedAt = DateTime.UtcNow
        });
        await _host.Db.SaveChangesAsync();

        await _service.EvaluatePlayerAsync(player.Id);

        (await HasAchievementAsync(player.Id, AchievementCatalog.Rich100)).Should().BeFalse();
    }

    [Fact]
    public async Task OnEventFinishedAsync_QuizWinner_GetsAchievement()
    {
        User winner = await SeedUserAsync();
        User loser = await SeedUserAsync();
        (_, EventSession session) = await SeedSessionAsync("quiz");

        await SeedSubmissionAsync(session.Id, winner.Id, score: 30);
        await SeedSubmissionAsync(session.Id, loser.Id, score: 10);

        await _service.OnEventFinishedAsync(session.Id, "quiz");

        (await HasAchievementAsync(winner.Id, AchievementCatalog.QuizWinner)).Should().BeTrue();
        (await HasAchievementAsync(loser.Id, AchievementCatalog.QuizWinner)).Should().BeFalse();
    }

    [Fact]
    public async Task OnEventFinishedAsync_OtherType_DoesNothing()
    {
        User player = await SeedUserAsync();
        (_, EventSession session) = await SeedSessionAsync("word_rush");
        await SeedSubmissionAsync(session.Id, player.Id, score: 30);

        await _service.OnEventFinishedAsync(session.Id, "word_rush");

        (await HasAchievementAsync(player.Id, AchievementCatalog.QuizWinner)).Should().BeFalse();
    }

    [Fact]
    public async Task OnPhotoLikedAsync_FiveLikes_UnlocksPhotoLoved()
    {
        User author = await SeedUserAsync();
        Guid photoId = await SeedLikedPhotoAsync(author.Id, likes: 5);

        await _service.OnPhotoLikedAsync(photoId);

        (await HasAchievementAsync(author.Id, AchievementCatalog.PhotoLoved)).Should().BeTrue();
    }

    [Fact]
    public async Task OnPhotoLikedAsync_FewLikes_DoesNothing()
    {
        User author = await SeedUserAsync();
        Guid photoId = await SeedLikedPhotoAsync(author.Id, likes: 3);

        await _service.OnPhotoLikedAsync(photoId);

        (await HasAchievementAsync(author.Id, AchievementCatalog.PhotoLoved)).Should().BeFalse();
    }

    [Fact]
    public async Task OnBingoLineAsync_UnlocksBingoLine()
    {
        User player = await SeedUserAsync();

        await _service.OnBingoLineAsync(player.Id);

        (await HasAchievementAsync(player.Id, AchievementCatalog.BingoLine)).Should().BeTrue();
    }
}
