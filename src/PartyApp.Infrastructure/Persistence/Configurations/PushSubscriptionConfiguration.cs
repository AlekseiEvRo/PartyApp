using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence.Configurations;

public class PushSubscriptionConfiguration: IEntityTypeConfiguration<PushSubscription>
{
    public void Configure(EntityTypeBuilder<PushSubscription> builder)
    {
        builder.Property(s => s.Endpoint).HasMaxLength(1000).IsRequired();
        builder.HasIndex(s => s.Endpoint).IsUnique();

        builder.Property(s => s.P256dh).HasMaxLength(200).IsRequired();
        builder.Property(s => s.Auth).HasMaxLength(100).IsRequired();
        builder.Property(s => s.UserAgent).HasMaxLength(300);

        builder.HasIndex(s => s.UserId);

        builder.HasOne(s => s.User)
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}