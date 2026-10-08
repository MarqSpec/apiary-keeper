using ApiaryCopilot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ApiaryCopilot.Infrastructure;

/// <summary>
/// EF Core mapping of inspections: one row each, the findings in a single jsonb column so that changing the field set
/// (<see cref="InspectionFields"/>) never changes the schema.
/// </summary>
internal static class InspectionModelExtensions
{
    private static readonly ValueConverter<AccountId, Guid> AccountIdConverter = new(id => id.Value, value => new AccountId(value));
    private static readonly ValueConverter<ColonyId, Guid> ColonyIdConverter = new(id => id.Value, value => new ColonyId(value));
    private static readonly ValueConverter<InspectionId, Guid> InspectionIdConverter = new(id => id.Value, value => new InspectionId(value));

    public static ModelBuilder ConfigureInspections(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Inspection>(inspection =>
        {
            inspection.HasKey(i => i.InspectionId);
            inspection.Property(i => i.InspectionId).HasConversion(InspectionIdConverter).ValueGeneratedNever();
            inspection.Property(i => i.AccountId).HasConversion(AccountIdConverter).IsRequired();
            inspection.HasOne<Account>().WithMany().HasForeignKey(i => i.AccountId).OnDelete(DeleteBehavior.Restrict);
            inspection.Property(i => i.ColonyId).HasConversion(ColonyIdConverter).IsRequired();
            inspection.HasOne<Colony>().WithMany().HasForeignKey(i => i.ColonyId).OnDelete(DeleteBehavior.Restrict);
            inspection.HasIndex(i => new { i.ColonyId, i.InspectedOn, i.RecordedAt });
            inspection.Property(i => i.InspectedOn);
            inspection.Property(i => i.RecordedAt);
            inspection.Property(i => i.Findings)
                .HasConversion(findings => InspectionFindingsJson.Serialize(findings), json => InspectionFindingsJson.Deserialize(json))
                .HasColumnType("jsonb")
                .IsRequired();
            inspection.Property(i => i.Notes).HasMaxLength(InspectionDetails.NotesMaxLength);
        });

        return modelBuilder;
    }
}
