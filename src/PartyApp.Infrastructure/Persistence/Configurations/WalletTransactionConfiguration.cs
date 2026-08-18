using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence.Configurations;

public class WalletTransactionConfiguration: IEntityTypeConfiguration<WalletTransaction>
{
    public void Configure(EntityTypeBuilder<WalletTransaction> builder)
    {
        builder.HasIndex(t => t.WalletId);

        builder.Property(t => t.Description).HasMaxLength(500).IsRequired();

        builder.HasOne(t => t.RelatedSession)
            .WithMany()
            .HasForeignKey(t => t.RelatedSessionId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}