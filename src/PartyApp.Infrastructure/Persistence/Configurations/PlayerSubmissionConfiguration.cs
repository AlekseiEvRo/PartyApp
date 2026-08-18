using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence.Configurations;

public class PlayerSubmissionConfiguration: IEntityTypeConfiguration<PlayerSubmission>
{
    public void Configure(EntityTypeBuilder<PlayerSubmission> builder)
    {
        builder.HasIndex(s => new { s.SessionId, s.PlayerId });

        builder.Property(s => s.PayloadJson).IsRequired();
    }
}