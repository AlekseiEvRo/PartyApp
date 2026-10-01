using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence.Configurations;

public class PhotoLikeConfiguration: IEntityTypeConfiguration<PhotoLike>
{
    public void Configure(EntityTypeBuilder<PhotoLike> builder)
    {
        // Один лайк на игрока для каждого фото
        builder.HasIndex(l => new { l.PhotoId, l.UserId }).IsUnique();

        builder.HasOne(l => l.Photo)
            .WithMany(p => p.Likes)
            .HasForeignKey(l => l.PhotoId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}