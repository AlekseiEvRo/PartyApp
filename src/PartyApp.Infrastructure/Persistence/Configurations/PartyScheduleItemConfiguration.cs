using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence.Configurations;

public class PartyScheduleItemConfiguration : IEntityTypeConfiguration<PartyScheduleItem>
{
    public void Configure(EntityTypeBuilder<PartyScheduleItem> builder)
    {
        builder.HasOne(i => i.Party)
            .WithMany()
            .HasForeignKey(i => i.PartyId)
            .OnDelete(DeleteBehavior.Cascade);

        // Пункт сценария — лишь план: удаление определения убирает его из очередей
        builder.HasOne(i => i.Definition)
            .WithMany()
            .HasForeignKey(i => i.DefinitionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(i => new { i.PartyId, i.Order });
    }
}
