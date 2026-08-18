using Microsoft.EntityFrameworkCore;

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
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
    
    // Автоматический bump concurrency-токена при изменении
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BumpVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        BumpVersions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void BumpVersions()
    {
        foreach (var entry in ChangeTracker.Entries<IHasConcurrency>())
        {
            if (entry.State == EntityState.Modified)
            {
                entry.Entity.Version++;
            }
        }
    }
    
}