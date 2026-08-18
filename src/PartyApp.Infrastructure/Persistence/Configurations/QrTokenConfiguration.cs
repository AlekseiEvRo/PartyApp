using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence.Configurations;

public class QrTokenConfiguration: IEntityTypeConfiguration<QrToken>
{
    public void Configure(EntityTypeBuilder<QrToken> builder)
    {
        builder.Property(t => t.Code).HasMaxLength(100).IsRequired();
        builder.HasIndex(t => t.Code).IsUnique();
    }
}