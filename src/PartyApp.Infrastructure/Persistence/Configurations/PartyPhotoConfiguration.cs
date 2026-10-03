using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence.Configurations;

public class PartyPhotoConfiguration: IEntityTypeConfiguration<PartyPhoto>
{
    public void Configure(EntityTypeBuilder<PartyPhoto> builder)
    {
        builder.Property(p => p.StoragePath).HasMaxLength(500).IsRequired();
        builder.Property(p => p.OriginalFileName).HasMaxLength(255).IsRequired();
        builder.Property(p => p.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(p => p.Caption).HasMaxLength(200);

        builder.HasIndex(p => p.UploadedById);
    }
}