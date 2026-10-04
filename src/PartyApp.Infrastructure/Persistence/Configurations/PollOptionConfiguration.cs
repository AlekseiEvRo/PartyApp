using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence.Configurations;

public class PollOptionConfiguration : IEntityTypeConfiguration<PollOption>
{
    public void Configure(EntityTypeBuilder<PollOption> builder)
    {
        builder.HasOne(o => o.Definition)
            .WithMany()
            .HasForeignKey(o => o.DefinitionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(o => new { o.PollId, o.Order });
    }
}
