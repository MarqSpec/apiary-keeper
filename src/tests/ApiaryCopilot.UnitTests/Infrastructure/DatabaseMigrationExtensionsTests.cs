using ApiaryCopilot.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ApiaryCopilot.UnitTests.Infrastructure;

public sealed class DatabaseMigrationExtensionsTests
{
    [Fact] // invariant: local development starts on the current schema
    public async Task MigrateDatabaseInDevelopmentAsync_ShouldMigrate_WhenTheEnvironmentIsDevelopment()
    {
        var migrator = A.Fake<IDatabaseMigrator>();
        using var cancellation = new CancellationTokenSource();

        await ProviderWith(migrator).MigrateDatabaseInDevelopmentAsync(EnvironmentNamed(Environments.Development), cancellation.Token);

        A.CallTo(() => migrator.MigrateAsync(cancellation.Token)).MustHaveHappenedOnceExactly();
    }

    [Theory] // invariant: a deployed database is never migrated by the application's own startup
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task MigrateDatabaseInDevelopmentAsync_ShouldNotMigrate_WhenTheEnvironmentIsNotDevelopment(string environmentName)
    {
        var migrator = A.Fake<IDatabaseMigrator>();

        await ProviderWith(migrator).MigrateDatabaseInDevelopmentAsync(EnvironmentNamed(environmentName), CancellationToken.None);

        A.CallTo(migrator).MustNotHaveHappened();
    }

    private static ServiceProvider ProviderWith(IDatabaseMigrator migrator) =>
        new ServiceCollection().AddScoped(_ => migrator).BuildServiceProvider();

    private static IHostEnvironment EnvironmentNamed(string name)
    {
        var environment = A.Fake<IHostEnvironment>();
        A.CallTo(() => environment.EnvironmentName).Returns(name);
        return environment;
    }
}
