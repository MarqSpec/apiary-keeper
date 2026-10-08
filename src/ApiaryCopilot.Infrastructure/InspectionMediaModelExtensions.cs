using ApiaryCopilot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ApiaryCopilot.Infrastructure;

/// <summary>
/// EF Core mapping of inspection media: one row per attached object, its bucket key unique so an object is attached at
/// most once.
/// </summary>
internal static class InspectionMediaModelExtensions
{
    /// <summary>The unique index on the object key, named so a lost race on it is recognised.</summary>
    public const string ObjectKeyIndexName = "IX_InspectionMedia_ObjectKey";

    // accounts/{36}/inspections/{36}/{32}: 127 characters; the column leaves room without accepting anything Parse refuses.
    private const int ObjectKeyMaxLength = 200;

    private static readonly ValueConverter<AccountId, Guid> AccountIdConverter = new(id => id.Value, value => new AccountId(value));
    private static readonly ValueConverter<InspectionId, Guid> InspectionIdConverter = new(id => id.Value, value => new InspectionId(value));
    private static readonly ValueConverter<MediaId, Guid> MediaIdConverter = new(id => id.Value, value => new MediaId(value));
    private static readonly ValueConverter<MediaObjectKey, string> ObjectKeyConverter = new(
        key => key.Value,
        value => FromStored(value));

    public static ModelBuilder ConfigureInspectionMedia(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InspectionMedia>(media =>
        {
            media.HasKey(m => m.MediaId);
            media.Property(m => m.MediaId).HasConversion(MediaIdConverter).ValueGeneratedNever();
            media.Property(m => m.AccountId).HasConversion(AccountIdConverter).IsRequired();
            media.HasOne<Account>().WithMany().HasForeignKey(m => m.AccountId).OnDelete(DeleteBehavior.Restrict);
            media.Property(m => m.InspectionId).HasConversion(InspectionIdConverter).IsRequired();
            media.HasOne<Inspection>().WithMany().HasForeignKey(m => m.InspectionId).OnDelete(DeleteBehavior.Restrict);
            media.HasIndex(m => new { m.InspectionId, m.AttachedAt });
            media.Property(m => m.ObjectKey).HasConversion(ObjectKeyConverter).HasMaxLength(ObjectKeyMaxLength).IsRequired();
            media.HasIndex(m => m.ObjectKey).IsUnique().HasDatabaseName(ObjectKeyIndexName);
            media.Property(m => m.ContentType).HasMaxLength(InspectionMedia.ContentTypeMaxLength).IsRequired();
            media.Property(m => m.SizeBytes);
            media.Property(m => m.AttachedAt);
        });

        return modelBuilder;
    }

    private static MediaObjectKey FromStored(string value) =>
        MediaObjectKey.Parse(value) ?? throw new InvalidOperationException("A stored media object key is malformed.");
}
