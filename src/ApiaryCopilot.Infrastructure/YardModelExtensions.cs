using ApiaryCopilot.Domain;
using Microsoft.EntityFrameworkCore;

namespace ApiaryCopilot.Infrastructure;

/// <summary>EF Core mapping of <see cref="Yard"/>: one row per yard, its details and landowner as columns.</summary>
internal static class YardModelExtensions
{
    // Owned types rather than complex types: the unit tier's in-memory provider supports only the former.
    public static ModelBuilder ConfigureYards(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Yard>(yard =>
        {
            yard.HasKey(y => y.YardId);
            yard.Property(y => y.YardId)
                .HasConversion(id => id.Value, value => new YardId(value))
                .ValueGeneratedNever();
            yard.Property(y => y.AccountId)
                .HasConversion(id => id.Value, value => new AccountId(value))
                .IsRequired();
            yard.HasOne<Account>()
                .WithMany()
                .HasForeignKey(y => y.AccountId)
                .OnDelete(DeleteBehavior.Restrict);

            yard.OwnsOne(y => y.Details, details =>
            {
                details.Property(d => d.Name).HasColumnName("Name").HasMaxLength(YardDetails.NameMaxLength).IsRequired();
                details.OwnsOne(d => d.Location, location =>
                {
                    location.Property(l => l.Latitude).HasColumnName("Latitude");
                    location.Property(l => l.Longitude).HasColumnName("Longitude");
                });
                details.Navigation(d => d.Location).IsRequired();
                details.Property(d => d.State).HasColumnName("State").HasConversion<string>().HasMaxLength(32);
                details.Property(d => d.County).HasColumnName("County").HasMaxLength(YardDetails.CountyMaxLength).IsRequired();
                details.Property(d => d.AccessNotes).HasColumnName("AccessNotes").HasMaxLength(YardDetails.NotesMaxLength);
                details.Property(d => d.ForageNotes).HasColumnName("ForageNotes").HasMaxLength(YardDetails.NotesMaxLength);
                details.OwnsOne(d => d.Landowner, landowner =>
                {
                    landowner.Property(l => l.Contact).HasColumnName("LandownerContact").HasMaxLength(Landowner.ContactMaxLength).IsRequired();
                    landowner.Property(l => l.Terms).HasColumnName("LandownerTerms").HasConversion<string>().HasMaxLength(32);
                    landowner.Property(l => l.NearbyCrops).HasColumnName("NearbyCrops").HasMaxLength(Landowner.NearbyCropsMaxLength);
                    landowner.Property(l => l.SprayContact).HasColumnName("SprayContact").HasMaxLength(Landowner.ContactMaxLength);
                });
                details.Navigation(d => d.Landowner).IsRequired();
            });
            yard.Navigation(y => y.Details).IsRequired();
        });
        return modelBuilder;
    }
}
