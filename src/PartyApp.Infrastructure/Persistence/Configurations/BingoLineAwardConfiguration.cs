using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence.Configurations;

public class BingoLineAwardConfiguration: IEntityTypeConfiguration<BingoLineAward>
{
    public void Configure(EntityTypeBuilder<BingoLineAward> builder)
    {
        // Бонус за линию получает каждый собравший её игрок — по разу на линию
        builder.HasIndex(a => new { a.SessionId, a.LineIndex, a.PlayerId }).IsUnique();

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