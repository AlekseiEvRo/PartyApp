using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence.Configurations;

public class LotConfiguration: IEntityTypeConfiguration<Lot>
{
    public void Configure(EntityTypeBuilder<Lot> builder)
    {
        builder.Property(l => l.Name).HasMaxLength(120).IsRequired();
        builder.Property(l => l.Description).HasMaxLength(500);

        builder.HasIndex(l => new { l.Status, l.EndsAt });

        builder.HasOne(l => l.Winner)
            .WithMany()
            .HasForeignKey(l => l.WinnerId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}