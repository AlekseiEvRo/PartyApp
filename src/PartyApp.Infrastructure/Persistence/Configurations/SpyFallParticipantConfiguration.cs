using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence.Configurations;

public class SpyFallParticipantConfiguration: IEntityTypeConfiguration<SpyFallParticipant>
{
    public void Configure(EntityTypeBuilder<SpyFallParticipant> builder)
    {
        // Один участник на игрока в раунде; голос хранится в той же записи и меняется
        builder.HasIndex(p => new { p.SessionId, p.UserId }).IsUnique();

        builder.HasOne(p => p.Session)
            .WithMany()
            .HasForeignKey(p => p.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(p => p.User)
            .WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}