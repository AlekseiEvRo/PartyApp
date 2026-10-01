using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence.Configurations;

public class PurchaseConfiguration: IEntityTypeConfiguration<Purchase>
{
    public void Configure(EntityTypeBuilder<Purchase> builder)
    {
        builder.HasIndex(p => p.PlayerId);
        builder.HasIndex(p => p.CreatedAt);

        // Историю покупок не удаляем вместе с товаром: удаление приза запрещено,
        // пока есть покупки (проверяется в эндпоинте)
        builder.HasOne(p => p.ShopItem)
            .WithMany()
            .HasForeignKey(p => p.ShopItemId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}