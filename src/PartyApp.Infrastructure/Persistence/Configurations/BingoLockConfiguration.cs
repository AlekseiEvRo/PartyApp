using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence.Configurations;

public class BingoLockConfiguration: IEntityTypeConfiguration<BingoLock>
{
    public void Configure(EntityTypeBuilder<BingoLock> builder)
    {
        // Приём предсказаний блокируется один раз на сессию
        builder.HasIndex(l => l.SessionId).IsUnique();

        builder.HasOne(l => l.Session)
            .WithMany()
            .HasForeignKey(l => l.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
