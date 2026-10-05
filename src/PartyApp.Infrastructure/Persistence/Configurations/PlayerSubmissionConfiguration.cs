using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence.Configurations;

public class PlayerSubmissionConfiguration: IEntityTypeConfiguration<PlayerSubmission>
{
    public void Configure(EntityTypeBuilder<PlayerSubmission> builder)
    {
        builder.HasIndex(s => new { s.SessionId, s.PlayerId });

        // Номера билетов лототрона уникальны внутри сессии; у остальных ивентов номер null,
        // а SQLite считает NULL-значения разными, поэтому индекс не мешает другим типам.
        builder.HasIndex(s => new { s.SessionId, s.TicketNumber }).IsUnique();

        builder.Property(s => s.PayloadJson).IsRequired();
    }
}