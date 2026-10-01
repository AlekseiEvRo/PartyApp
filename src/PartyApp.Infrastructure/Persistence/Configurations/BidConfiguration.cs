using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence.Configurations;

public class BidConfiguration: IEntityTypeConfiguration<Bid>
{
    public void Configure(EntityTypeBuilder<Bid> builder)
    {
        // Одна ставка на игрока, её можно повышать
        builder.HasIndex(b => new { b.LotId, b.PlayerId }).IsUnique();

        builder.HasOne(b => b.Lot)
            .WithMany(l => l.Bids)
            .HasForeignKey(b => b.LotId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}