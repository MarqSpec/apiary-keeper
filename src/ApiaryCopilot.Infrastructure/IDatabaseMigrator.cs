namespace ApiaryCopilot.Infrastructure;

/// <summary>Brings the database schema up to the committed migrations.</summary>
public interface IDatabaseMigrator
{
    /// <summary>Applies every migration the database has not had yet.</summary>
    Task MigrateAsync(CancellationToken cancellationToken);
}
