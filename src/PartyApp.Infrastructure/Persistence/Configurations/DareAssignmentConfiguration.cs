using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using PartyApp.Domain.Entities;

namespace PartyApp.Infrastructure.Persistence.Configurations;

public class DareAssignmentConfiguration: IEntityTypeConfiguration<DareAssignment>
{
    public void Configure(EntityTypeBuilder<DareAssignment> builder)
    {
        // Ровно один фант на игрока в рамках сессии
        builder.HasIndex(a => new { a.SessionId, a.PlayerId }).IsUnique();

        builder.Property(a => a.Task).HasMaxLength(300).IsRequired();

        builder.HasOne(a => a.Session)
            .WithMany()
            .HasForeignKey(a => a.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}