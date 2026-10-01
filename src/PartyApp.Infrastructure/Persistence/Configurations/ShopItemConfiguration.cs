using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence.Configurations;

public class ShopItemConfiguration: IEntityTypeConfiguration<ShopItem>
{
    public void Configure(EntityTypeBuilder<ShopItem> builder)
    {
        builder.Property(i => i.Name).HasMaxLength(100).IsRequired();
        builder.Property(i => i.Description).HasMaxLength(500);

        builder.HasIndex(i => i.IsActive);
    }
}