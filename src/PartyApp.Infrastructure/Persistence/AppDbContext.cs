using Microsoft.EntityFrameworkCore;

namespace PartyApp.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    // DbSet'ы в следующем шаге. Пока можно оставить так:
    // public DbSet<User> Users => Set<User>();
    // public DbSet<EventDefinition> EventDefinitions => Set<EventDefinition>();
    // public DbSet<EventSession> EventSessions => Set<EventSession>();
    // ...
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        // Здесь позже будут Fluent API конфигурации
        // modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
    
}