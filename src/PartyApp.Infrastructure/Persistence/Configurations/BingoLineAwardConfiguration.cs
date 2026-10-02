using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence.Configurations;

public class BingoLineAwardConfiguration: IEntityTypeConfiguration<BingoLineAward>
{
    public void Configure(EntityTypeBuilder<BingoLineAward> builder)
    {
        // Каждая линия разыгрывается один раз за сессию
        builder.HasIndex(a => new { a.SessionId, a.LineIndex }).IsUnique();

        builder.HasOne(a => a.Session)
            .WithMany()
            .HasForeignKey(a => a.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(a => a.Player)
            .WithMany()
            .HasForeignKey(a => a.PlayerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}