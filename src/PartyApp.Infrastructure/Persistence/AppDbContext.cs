using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

using PartyApp.Domain.Common;
using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<WalletTransaction> WalletTransactions => Set<WalletTransaction>();
    public DbSet<EventDefinition> EventDefinitions => Set<EventDefinition>();
    public DbSet<EventSession> EventSessions => Set<EventSession>();
    public DbSet<PlayerSubmission> PlayerSubmissions => Set<PlayerSubmission>();
    public DbSet<QrToken> QrTokens => Set<QrToken>();
    public DbSet<PartyPhoto> PartyPhotos => Set<PartyPhoto>();
    public DbSet<PhotoLike> PhotoLikes => Set<PhotoLike>();
    public DbSet<Wish> Wishes => Set<Wish>();
    public DbSet<ShopItem> ShopItems => Set<ShopItem>();
    public DbSet<Purchase> Purchases => Set<Purchase>();
    public DbSet<Lot> Lots => Set<Lot>();
    public DbSet<Bid> Bids => Set<Bid>();
    public DbSet<ScreenSettings> ScreenSettings => Set<ScreenSettings>();
    public DbSet<RewardSettings> RewardSettings => Set<RewardSettings>();
    public DbSet<DareAssignment> DareAssignments => Set<DareAssignment>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // SQLite не хранит Kind у DateTime, поэтому после чтения помечаем значения как UTC:
        // иначе они уезжают в JSON без суффикса Z и клиенты показывают их как локальное время.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime))
                {
                    property.SetValueConverter(new ValueConverter<DateTime, DateTime>(
                        v => v,
                        v => DateTime.SpecifyKind(v, DateTimeKind.Utc)));
                }
                else if (property.ClrType == typeof(DateTime?))
                {
                    property.SetValueConverter(new ValueConverter<DateTime?, DateTime?>(
                        v => v,
                        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v));
                }
            }
        }
    }
    
}