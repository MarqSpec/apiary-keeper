using ApiaryCopilot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ApiaryCopilot.Infrastructure;

/// <summary>EF Core mapping of <see cref="Account"/>, its <see cref="User"/>s and every row's <see cref="IRowStamped"/> stamps.</summary>
internal static class AccountModelExtensions
{
    private static readonly ValueConverter<UserId, Guid> UserIdConverter = new(id => id.Value, value => new UserId(value));

    public static ModelBuilder ConfigureAccounts(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(account =>
        {
            account.HasKey(o => o.AccountId);
            account.Property(o => o.AccountId)
                .HasConversion(id => id.Value, value => new AccountId(value))
                .ValueGeneratedNever();
            account.Property(o => o.Name)
                .HasMaxLength(Account.NameMaxLength)
                .IsRequired();
        });
        return modelBuilder;
    }

    public static ModelBuilder ConfigureUsers(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(user =>
        {
            user.HasKey(u => u.UserId);
            user.Property(u => u.UserId).HasConversion(UserIdConverter).ValueGeneratedNever();
            user.Property(u => u.AccountId)
                .HasConversion(id => id.Value, value => new AccountId(value))
                .IsRequired();
            user.HasOne<Account>().WithMany().HasForeignKey(u => u.AccountId).OnDelete(DeleteBehavior.Restrict);
            user.Property(u => u.Name).HasMaxLength(User.NameMaxLength).IsRequired();
            // Nullable: an invited user has no subject until it accepts. Unique across accounts, not per account: a
            // Keycloak user is one person in one account (D-22), so a second row anywhere would let one sign-in act as two.
            user.Property(u => u.ExternalSubject).HasMaxLength(User.ExternalSubjectMaxLength);
            user.HasIndex(u => u.ExternalSubject).IsUnique();
        });
        return modelBuilder;
    }

    // The stamps are mapped by name: the domain exposes them read-only, and only the context's save writes them.
    public static void ConfigureRowStamps<TEntity>(this ModelBuilder modelBuilder)
        where TEntity : class, IRowStamped
    {
        modelBuilder.Entity<TEntity>(entity =>
        {
            entity.Property<UserId>(nameof(IRowStamped.CreatedBy)).HasConversion(UserIdConverter).IsRequired();
            entity.Property<DateTimeOffset>(nameof(IRowStamped.CreatedAt)).IsRequired();
            entity.Property<UserId>(nameof(IRowStamped.LastModifiedBy)).HasConversion(UserIdConverter).IsRequired();
            entity.Property<DateTimeOffset>(nameof(IRowStamped.LastModifiedAt)).IsRequired();
            entity.HasOne<User>().WithMany().HasForeignKey(nameof(IRowStamped.CreatedBy)).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>().WithMany().HasForeignKey(nameof(IRowStamped.LastModifiedBy)).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
