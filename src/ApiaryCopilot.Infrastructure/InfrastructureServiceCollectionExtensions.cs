using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ApiaryCopilot.Infrastructure;

/// <summary>Registers the persistence layer.</summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// The connection string's name: set it as <c>ConnectionStrings__ApiaryCopilot</c> in the environment
    /// (DEPLOYMENT.md §3).
    /// </summary>
    public const string ConnectionStringName = "ApiaryCopilot";

    /// <summary>
    /// Registers <see cref="ApiaryCopilotDbContext"/> on PostgreSQL, <see cref="IDatabaseMigrator"/>,
    /// <see cref="IYardStore"/>, <see cref="IColonyStore"/>, <see cref="IInspectionStore"/>, <see cref="IInspectionMediaStore"/>, <see cref="ITenantDirectory"/>,
    /// <see cref="IUserStore"/>, the Data Protection key ring's <see cref="IDataProtectionKeyStore"/> and, unless the host registered one, the system
    /// <see cref="TimeProvider"/>. A host that registers its own <see cref="ICurrentAccount"/> or <see cref="ICurrentUser"/>
    /// first keeps it; otherwise there is none: every query returns nothing, and nothing can be written.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string? connectionString)
    {
        services.TryAddScoped<ICurrentAccount, NoCurrentAccount>();
        services.TryAddScoped<ICurrentUser, NoCurrentUser>();
        services.AddDbContext<ApiaryCopilotDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IDatabaseMigrator, EfCoreDatabaseMigrator>();
        services.AddScoped<IYardStore, EfCoreYardStore>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IColonyStore, EfCoreColonyStore>();
        services.AddScoped<IInspectionStore, EfCoreInspectionStore>();
        services.AddScoped<IInspectionMediaStore, EfCoreInspectionMediaStore>();
        services.AddScoped<ITenantDirectory, EfCoreTenantDirectory>();
        services.AddScoped<IUserStore, EfCoreUserStore>();
        services.AddSingleton<IDataProtectionKeyStore>(new PostgresDataProtectionKeyStore(connectionString));
        return services;
    }
}
