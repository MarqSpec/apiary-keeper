using ApiaryCopilot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ApiaryCopilot.Infrastructure;

/// <summary>
/// EF Core mapping of colonies, their queens and their lineage events: <c>ColonyEvents</c> is the append-only history,
/// <c>Colonies</c> and <c>Queens</c> the read models projected from it (ARCHITECTURE.md D-2).
/// </summary>
internal static class ColonyModelExtensions
{
    /// <summary>The unique index that keeps a tag id to one colony per account, as the migrations name it.</summary>
    public const string TagIdIndexName = "IX_Colonies_AccountId_TagId";

    private static readonly ValueConverter<AccountId, Guid> AccountIdConverter = new(id => id.Value, value => new AccountId(value));
    private static readonly ValueConverter<YardId, Guid> YardIdConverter = new(id => id.Value, value => new YardId(value));
    private static readonly ValueConverter<ColonyId, Guid> ColonyIdConverter = new(id => id.Value, value => new ColonyId(value));
    private static readonly ValueConverter<QueenId, Guid> QueenIdConverter = new(id => id.Value, value => new QueenId(value));
    private static readonly ValueConverter<ColonyEventId, Guid> ColonyEventIdConverter = new(id => id.Value, value => new ColonyEventId(value));

    public static ModelBuilder ConfigureColonies(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Colony>(colony =>
        {
            colony.HasKey(c => c.ColonyId);
            colony.Property(c => c.ColonyId).HasConversion(ColonyIdConverter).ValueGeneratedNever();
            colony.Property(c => c.AccountId).HasConversion(AccountIdConverter).IsRequired();
            colony.HasOne<Account>().WithMany().HasForeignKey(c => c.AccountId).OnDelete(DeleteBehavior.Restrict);
            colony.Property(c => c.TagId).HasMaxLength(ColonyProfile.IdMaxLength).IsRequired();
            colony.HasIndex(c => new { c.AccountId, c.TagId }).IsUnique().HasDatabaseName(TagIdIndexName);
            colony.Property(c => c.HiveNumber).HasMaxLength(ColonyProfile.IdMaxLength).IsRequired();
            colony.Property(c => c.HiveType).HasMaxLength(ColonyProfile.HiveTypeMaxLength).IsRequired();
            colony.Property(c => c.Origin).HasConversion<string>().HasMaxLength(32);
            colony.Property(c => c.ParentColonyId).HasConversion(ColonyIdConverter);
            colony.HasOne<Colony>().WithMany().HasForeignKey(c => c.ParentColonyId).OnDelete(DeleteBehavior.Restrict);
            colony.Property(c => c.YardId).HasConversion(YardIdConverter).IsRequired();
            colony.HasOne<Yard>().WithMany().HasForeignKey(c => c.YardId).OnDelete(DeleteBehavior.Restrict);
            colony.Property(c => c.Status).HasConversion<string>().HasMaxLength(32);
            colony.Property(c => c.MergedIntoColonyId).HasConversion(ColonyIdConverter);
            colony.HasOne<Colony>().WithMany().HasForeignKey(c => c.MergedIntoColonyId).OnDelete(DeleteBehavior.Restrict);
            colony.Property(c => c.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<Queen>(queen =>
        {
            queen.HasKey(q => q.QueenId);
            queen.Property(q => q.QueenId).HasConversion(QueenIdConverter).ValueGeneratedNever();
            queen.Property(q => q.AccountId).HasConversion(AccountIdConverter).IsRequired();
            queen.HasOne<Account>().WithMany().HasForeignKey(q => q.AccountId).OnDelete(DeleteBehavior.Restrict);
            queen.Property(q => q.ColonyId).HasConversion(ColonyIdConverter).IsRequired();
            queen.HasOne<Colony>().WithMany().HasForeignKey(q => q.ColonyId).OnDelete(DeleteBehavior.Restrict);
            queen.OwnsOne(q => q.Details, details =>
            {
                details.Property(d => d.Source).HasColumnName("Source").HasMaxLength(QueenDetails.SourceMaxLength).IsRequired();
                details.Property(d => d.MarkColour).HasColumnName("MarkColour").HasConversion<string>().HasMaxLength(32);
                details.Property(d => d.MarkYear).HasColumnName("MarkYear");
                details.Property(d => d.IntroducedOn).HasColumnName("IntroducedOn");
            });
            queen.Navigation(q => q.Details).IsRequired();
            queen.Property(q => q.ReplacedByQueenId).HasConversion(QueenIdConverter);
            queen.HasOne<Queen>().WithMany().HasForeignKey(q => q.ReplacedByQueenId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ColonyEvent>(colonyEvent =>
        {
            colonyEvent.HasKey(e => e.EventId);
            colonyEvent.Property(e => e.EventId).HasConversion(ColonyEventIdConverter).ValueGeneratedNever();
            colonyEvent.Property(e => e.AccountId).HasConversion(AccountIdConverter).IsRequired();
            colonyEvent.HasOne<Account>().WithMany().HasForeignKey(e => e.AccountId).OnDelete(DeleteBehavior.Restrict);
            colonyEvent.Property(e => e.ColonyId).HasConversion(ColonyIdConverter).IsRequired();
            colonyEvent.HasOne<Colony>().WithMany().HasForeignKey(e => e.ColonyId).OnDelete(DeleteBehavior.Restrict);
            colonyEvent.Property(e => e.RelatedColonyId).HasConversion(ColonyIdConverter);
            colonyEvent.HasOne<Colony>().WithMany().HasForeignKey(e => e.RelatedColonyId).OnDelete(DeleteBehavior.Restrict);
            colonyEvent.Property(e => e.OccurredOn);
            colonyEvent.Property(e => e.RecordedAt);
            colonyEvent.HasIndex(e => new { e.ColonyId, e.RecordedAt });
            colonyEvent.Property(e => e.Data)
                .HasConversion(data => ColonyEventJson.Serialize(data), json => ColonyEventJson.Deserialize(json))
                .HasColumnType("jsonb")
                .IsRequired();
        });

        return modelBuilder;
    }
}
