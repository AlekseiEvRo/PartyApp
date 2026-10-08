using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence.Configurations;

public class SpyFallRoundConfiguration: IEntityTypeConfiguration<SpyFallRound>
{
    public void Configure(EntityTypeBuilder<SpyFallRound> builder)
    {
        // Один раунд на сессию ивента
        builder.HasIndex(r => r.SessionId).IsUnique();

        builder.HasOne(r => r.Session)
            .WithMany()
            .HasForeignKey(r => r.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.SpyUser)
            .WithMany()
            .HasForeignKey(r => r.SpyUserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}