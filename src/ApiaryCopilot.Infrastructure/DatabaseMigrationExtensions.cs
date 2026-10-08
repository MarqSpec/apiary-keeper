using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ApiaryCopilot.Infrastructure;

/// <summary>Startup migration of the database.</summary>
public static class DatabaseMigrationExtensions
{
    /// <summary>
    /// Applies pending migrations when <paramref name="environment"/> is Development, and does nothing anywhere
    /// else, so a deployed database is never migrated by the application starting.
    /// </summary>
    public static async Task MigrateDatabaseInDevelopmentAsync(
        this IServiceProvider services, IHostEnvironment environment, CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
        {
            return;
        }

        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseMigrator>().MigrateAsync(cancellationToken);
    }
}
