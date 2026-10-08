using System.Reflection;
using ApiaryCopilot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ApiaryCopilot.Infrastructure;

/// <summary>
/// The application's database. Every query is filtered to <see cref="ICurrentAccount"/>; with no current
/// account it returns nothing (NFR-8, ARCHITECTURE.md D-8). A save that would change or delete a recorded
/// <see cref="ColonyEvent"/> is refused (FR-6, D-2). Every row written is stamped with
/// <see cref="ICurrentUser"/> and the clock (<see cref="IRowStamped"/>), and nothing is written with no current
/// user. Never shared across calls.
/// </summary>
public sealed class ApiaryCopilotDbContext(
    DbContextOptions<ApiaryCopilotDbContext> options,
    ICurrentAccount currentAccount,
    ICurrentUser currentUser,
    TimeProvider clock) : DbContext(options)
{
    private static readonly MethodInfo FilterByAccountMethod = typeof(ApiaryCopilotDbContext)
        .GetMethod(nameof(FilterByAccount), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo ConfigureRowStampsMethod = typeof(AccountModelExtensions)
        .GetMethod(nameof(AccountModelExtensions.ConfigureRowStamps), BindingFlags.Public | BindingFlags.Static)!;

    /// <summary>The accounts the caller may see: its own.</summary>
    public DbSet<Account> Accounts => Set<Account>();

    /// <summary>The users of the caller's account.</summary>
    public DbSet<User> Users => Set<User>();

    /// <summary>The caller's account's yards.</summary>
    public DbSet<Yard> Yards => Set<Yard>();

    /// <summary>The caller's account's colonies, as projected from their events.</summary>
    public DbSet<Colony> Colonies => Set<Colony>();

    /// <summary>The caller's account's queens, current and replaced.</summary>
    public DbSet<Queen> Queens => Set<Queen>();

    /// <summary>The caller's account's lineage events: add only.</summary>
    public DbSet<ColonyEvent> ColonyEvents => Set<ColonyEvent>();

    /// <summary>The caller's account's inspections of its colonies.</summary>
    public DbSet<Inspection> Inspections => Set<Inspection>();

    /// <summary>The photos and videos attached to the caller's account's inspections.</summary>
    public DbSet<InspectionMedia> InspectionMedia => Set<InspectionMedia>();

    // Read per query: EF Core parameterizes a filter's reference to the context instance.
    private AccountId? CurrentAccountId => currentAccount.AccountId;

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder
            .ConfigureAccounts()
            .ConfigureUsers()
            .ConfigureYards()
            .ConfigureColonies()
            .ConfigureInspections()
            .ConfigureInspectionMedia();

        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(t => !t.IsOwned() && typeof(IAccountOwned).IsAssignableFrom(t.ClrType)))
        {
            FilterByAccountMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
        }

        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(t => !t.IsOwned() && typeof(IRowStamped).IsAssignableFrom(t.ClrType)).ToList())
        {
            ConfigureRowStampsMethod.MakeGenericMethod(entityType.ClrType).Invoke(null, [modelBuilder]);
        }
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// A recorded colony event was changed or deleted, or a stamped row was written with no current user; nothing is saved.
    /// </exception>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RefuseChangesToRecordedEvents();
        StampRows();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// A recorded colony event was changed or deleted, or a stamped row was written with no current user; nothing is saved.
    /// </exception>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RefuseChangesToRecordedEvents();
        StampRows();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // The second of three locks: no endpoint offers it, this refuses it, and a database trigger refuses it again.
    private void RefuseChangesToRecordedEvents()
    {
        if (ChangeTracker.Entries<ColonyEvent>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Colony events are append-only: a recorded event cannot be changed or deleted.");
        }
    }

    // Every added row gets all four stamps; every changed row - a change to an owned part such as a yard's details
    // included - gets the last-modified pair, and its created pair is put back as it was read.
    private void StampRows()
    {
        var written = ChangeTracker.Entries<IRowStamped>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified || HasChangedOwnedPart(e))
            .ToList();
        if (written.Count == 0)
        {
            return;
        }

        var userId = currentUser.UserId
            ?? throw new InvalidOperationException("No current user: a row cannot be written without the user who wrote it.");
        var now = clock.GetUtcNow();
        foreach (var entry in written)
        {
            if (entry.State is EntityState.Added)
            {
                entry.Property(nameof(IRowStamped.CreatedBy)).CurrentValue = userId;
                entry.Property(nameof(IRowStamped.CreatedAt)).CurrentValue = now;
            }
            else
            {
                foreach (var created in new[] { nameof(IRowStamped.CreatedBy), nameof(IRowStamped.CreatedAt) })
                {
                    var property = entry.Property(created);
                    property.CurrentValue = property.OriginalValue;
                    property.IsModified = false;
                }
            }

            entry.Property(nameof(IRowStamped.LastModifiedBy)).CurrentValue = userId;
            entry.Property(nameof(IRowStamped.LastModifiedAt)).CurrentValue = now;
        }
    }

    private static bool HasChangedOwnedPart(EntityEntry entry) =>
        entry.References.Any(r => r.TargetEntry is { } owned
            && owned.Metadata.IsOwned()
            && (owned.State is EntityState.Added or EntityState.Modified or EntityState.Deleted || HasChangedOwnedPart(owned)));

    // An instance method, so the filter captures this context and EF Core swaps in the querying one; a static
    // helper's closure would pin the first context's account into the cached model.
    private void FilterByAccount<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, IAccountOwned =>
        modelBuilder.Entity<TEntity>().HasQueryFilter(e => e.AccountId == CurrentAccountId);
}
