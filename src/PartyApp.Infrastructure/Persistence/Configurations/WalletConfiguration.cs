using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence.Configurations;

public class WalletConfiguration: IEntityTypeConfiguration<Wallet>
{
    public void Configure(EntityTypeBuilder<Wallet> builder)
    {
        builder.HasIndex(w => w.UserId).IsUnique();

        // Оптимистичная конкуренция
        builder.Property(w => w.Version).IsConcurrencyToken();
    }
}