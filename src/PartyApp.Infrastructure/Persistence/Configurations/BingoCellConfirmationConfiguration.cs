using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence.Configurations;

public class BingoCellConfirmationConfiguration: IEntityTypeConfiguration<BingoCellConfirmation>
{
    public void Configure(EntityTypeBuilder<BingoCellConfirmation> builder)
    {
        // Одну клетку сессии можно подтвердить один раз
        builder.HasIndex(c => new { c.SessionId, c.CellIndex }).IsUnique();

        builder.HasOne(c => c.Session)
            .WithMany()
            .HasForeignKey(c => c.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}