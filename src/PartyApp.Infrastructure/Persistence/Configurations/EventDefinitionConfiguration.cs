using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence.Configurations;

public class EventDefinitionConfiguration: IEntityTypeConfiguration<EventDefinition>
{
    public void Configure(EntityTypeBuilder<EventDefinition> builder)
    {
        builder.Property(d => d.Type).HasMaxLength(50).IsRequired();
        builder.HasIndex(d => d.Type);

        builder.Property(d => d.DisplayName).HasMaxLength(100).IsRequired();
        builder.Property(d => d.ConfigJson).IsRequired();
    }
}