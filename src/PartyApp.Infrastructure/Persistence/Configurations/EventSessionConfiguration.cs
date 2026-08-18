using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence.Configurations;

public class EventSessionConfiguration: IEntityTypeConfiguration<EventSession>
{
    public void Configure(EntityTypeBuilder<EventSession> builder)
    {
        builder.HasIndex(s => s.State);
        builder.HasIndex(s => s.DefinitionId);

        // Оптимистичная конкуренция
        builder.Property(s => s.Version).IsConcurrencyToken();
    }
}