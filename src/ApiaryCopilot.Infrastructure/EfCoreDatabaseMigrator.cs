using Microsoft.EntityFrameworkCore;

namespace ApiaryCopilot.Infrastructure;

internal sealed class EfCoreDatabaseMigrator(ApiaryCopilotDbContext db) : IDatabaseMigrator
{
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(db.Database.GetConnectionString()))
        {
            throw new InvalidOperationException(
                $"No database connection string: set ConnectionStrings__{InfrastructureServiceCollectionExtensions.ConnectionStringName} (README.md, DEPLOYMENT.md §3).");
        }

        await db.Database.MigrateAsync(cancellationToken);
    }
}
