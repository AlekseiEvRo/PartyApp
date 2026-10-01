using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence.Configurations;

public class WishConfiguration: IEntityTypeConfiguration<Wish>
{
    public void Configure(EntityTypeBuilder<Wish> builder)
    {
        builder.Property(w => w.Text).HasMaxLength(500).IsRequired();

        builder.HasIndex(w => w.CreatedAt);

        builder.HasOne(w => w.Player)
            .WithMany()
            .HasForeignKey(w => w.PlayerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}