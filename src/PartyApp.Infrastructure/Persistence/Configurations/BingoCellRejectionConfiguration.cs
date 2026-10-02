using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence.Configurations;

public class BingoCellRejectionConfiguration: IEntityTypeConfiguration<BingoCellRejection>
{
    public void Configure(EntityTypeBuilder<BingoCellRejection> builder)
    {
        // Одну клетку сессии можно отклонить один раз
        builder.HasIndex(c => new { c.SessionId, c.CellIndex }).IsUnique();

        builder.HasOne(c => c.Session)
            .WithMany()
            .HasForeignKey(c => c.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}