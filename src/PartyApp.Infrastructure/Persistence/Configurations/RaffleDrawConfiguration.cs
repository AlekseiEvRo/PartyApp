using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence.Configurations;

public class RaffleDrawConfiguration: IEntityTypeConfiguration<RaffleDraw>
{
    public void Configure(EntityTypeBuilder<RaffleDraw> builder)
    {
        // Один розыгрыш на сессию
        builder.HasIndex(d => d.SessionId).IsUnique();

        builder.HasOne(d => d.Session)
            .WithMany()
            .HasForeignKey(d => d.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}