using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence.Configurations;

public class AdminAuditLogConfiguration : IEntityTypeConfiguration<AdminAuditLog>
{
    public void Configure(EntityTypeBuilder<AdminAuditLog> builder)
    {
        builder.Property(a => a.Action).HasMaxLength(50).IsRequired();
        builder.Property(a => a.Details).HasMaxLength(500);

        // Админ и цель не удаляются, поэтому связи ограничиваем:
        // иначе SQLite попытается каскадно чистить журнал.
        builder.HasOne(a => a.Admin)
            .WithMany()
            .HasForeignKey(a => a.AdminId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.TargetUser)
            .WithMany()
            .HasForeignKey(a => a.TargetUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(a => a.CreatedAt);
    }
}
