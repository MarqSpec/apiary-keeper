using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ApiaryCopilot.UnitTests.Infrastructure;

public sealed class InfrastructureServiceCollectionExtensionsTests
{
    private const string SyntheticConnectionString = "Host=localhost;Database=apiary_copilot_test;Username=synthetic";

    [Fact] // boundary: until authentication supplies an account, the registered context sees nothing
    public void AddInfrastructure_ShouldRegisterNoCurrentAccount_WhenTheHostRegisteredNone()
    {
        using var provider = new ServiceCollection()
            .AddInfrastructure(SyntheticConnectionString)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ICurrentAccount>().AccountId.Should().BeNull();
    }

    [Fact] // invariant: the host's account (from the signed-in user) is never replaced by the default
    public void AddInfrastructure_ShouldKeepTheHostsCurrentAccount_WhenTheHostRegisteredOneFirst()
    {
        var hostAccount = A.Fake<ICurrentAccount>();
        A.CallTo(() => hostAccount.AccountId)
            .Returns(new AccountId(Guid.Parse("33333333-3333-4333-8333-333333333333")));
        using var provider = new ServiceCollection()
            .AddScoped(_ => hostAccount)
            .AddInfrastructure(SyntheticConnectionString)
            .BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ICurrentAccount>().Should().BeSameAs(hostAccount);
    }

    [Fact] // boundary: with no host-supplied user there is none, so nothing can be written (fails closed)
    public void AddInfrastructure_ShouldRegisterNoCurrentUser_WhenTheHostSuppliesNone()
    {
        using var provider = new ServiceCollection()
            .AddInfrastructure(SyntheticConnectionString)
            .BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ICurrentUser>().UserId.Should().BeNull();
    }

    [Fact] // invariant: the host's user (from the signed-in caller) is never replaced by the default
    public void AddInfrastructure_ShouldKeepTheHostsCurrentUser_WhenTheHostRegisteredOneFirst()
    {
        var hostUser = A.Fake<ICurrentUser>();
        A.CallTo(() => hostUser.UserId).Returns(new UserId(Guid.Parse("33333333-3333-4333-8333-333333333333")));
        using var provider = new ServiceCollection()
            .AddScoped(_ => hostUser)
            .AddInfrastructure(SyntheticConnectionString)
            .BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ICurrentUser>().Should().BeSameAs(hostUser);
    }

    [Fact] // regression risk: the context must reach PostgreSQL with the configured connection string (D-2)
    public void AddInfrastructure_ShouldConfigureNpgsqlWithTheConnectionString_WhenOneIsGiven()
    {
        using var provider = new ServiceCollection()
            .AddInfrastructure(SyntheticConnectionString)
            .BuildServiceProvider();
        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<ApiaryCopilotDbContext>();

        db.Database.ProviderName.Should().Be("Npgsql.EntityFrameworkCore.PostgreSQL");
        db.Database.GetConnectionString().Should().Be(SyntheticConnectionString);
    }

    [Fact] // regression risk: the yard endpoints resolve the store from the same graph tests compose
    public void AddInfrastructure_ShouldRegisterTheYardStore_WhenCalled()
    {
        using var provider = new ServiceCollection()
            .AddInfrastructure(SyntheticConnectionString)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IYardStore>().Should().BeOfType<EfCoreYardStore>();
    }

    [Fact] // regression risk: the colony endpoints resolve the store, and the store the system clock
    public void AddInfrastructure_ShouldRegisterTheColonyStoreAndTheSystemClock_WhenCalled()
    {
        using var provider = new ServiceCollection()
            .AddInfrastructure(SyntheticConnectionString)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IColonyStore>().Should().BeOfType<EfCoreColonyStore>();
        scope.ServiceProvider.GetRequiredService<TimeProvider>().Should().BeSameAs(TimeProvider.System);
    }

    [Fact] // regression risk: the inspection endpoints resolve the store
    public void AddInfrastructure_ShouldRegisterTheInspectionStore_WhenCalled()
    {
        using var provider = new ServiceCollection()
            .AddInfrastructure(SyntheticConnectionString)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IInspectionStore>().Should().BeOfType<EfCoreInspectionStore>();
    }

    [Fact] // regression risk: a model change shipped without its migration leaves the database behind
    public void HasPendingModelChanges_ShouldBeFalse_WhenTheCommittedMigrationsAreCurrent()
    {
        using var provider = new ServiceCollection()
            .AddInfrastructure(SyntheticConnectionString)
            .BuildServiceProvider();
        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<ApiaryCopilotDbContext>();

        db.Database.GetMigrations().Should().NotBeEmpty();
        db.Database.HasPendingModelChanges().Should().BeFalse();
    }
}
