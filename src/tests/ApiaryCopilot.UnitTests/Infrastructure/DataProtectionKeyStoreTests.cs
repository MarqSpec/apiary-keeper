using ApiaryCopilot.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace ApiaryCopilot.UnitTests.Infrastructure;

// The Data Protection key ring lives in PostgreSQL (ARCHITECTURE.md D-19) but outside the tenant model: a migration
// creates its table, the account-filtered context never maps it, and the store fails naming the setting it lacks. The
// SQL itself runs against PostgreSQL in the integration tier.
public sealed class DataProtectionKeyStoreTests
{
    private const string SyntheticConnectionString = "Host=localhost;Database=apiary_copilot_test;Username=synthetic";

    [Fact] // regression risk: the key manager is a singleton, so its store must be one too, resolvable outside a scope
    public void AddInfrastructure_ShouldRegisterThePostgresKeyStoreAsASingleton_WhenCalled()
    {
        using var provider = new ServiceCollection()
            .AddInfrastructure(SyntheticConnectionString)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var store = provider.GetRequiredService<IDataProtectionKeyStore>();

        store.Should().BeOfType<PostgresDataProtectionKeyStore>();
        provider.GetRequiredService<IDataProtectionKeyStore>().Should().BeSameAs(store);
    }

    [Theory] // boundary: with no database configured the key ring cannot be read, and the failure names the setting
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void GetAll_ShouldThrowNamingTheConnectionString_WhenNoneIsConfigured(string? connectionString)
    {
        var store = new PostgresDataProtectionKeyStore(connectionString);

        var act = () => store.GetAll();

        act.Should().Throw<InvalidOperationException>().WithMessage("*ConnectionStrings__ApiaryCopilot*");
    }

    [Fact] // boundary: an empty element is never stored, so a corrupt key ring cannot be written
    public void Add_ShouldRefuse_WhenTheElementIsBlank()
    {
        var store = new PostgresDataProtectionKeyStore(SyntheticConnectionString);

        var act = () => store.Add("key-1", " ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact] // invariant: a committed migration creates the key ring's table, so every database that migrates has it
    public void Migrations_ShouldCreateTheKeyRingTable_WhenApplied()
    {
        using var db = CreateContext();
        var migrations = db.GetService<IMigrationsAssembly>();

        var created = migrations.Migrations.Values
            .Select(type => migrations.CreateMigration(type, db.Database.ProviderName!))
            .SelectMany(m => m.UpOperations.OfType<CreateTableOperation>())
            .Where(t => t.Name == PostgresDataProtectionKeyStore.TableName)
            .ToList();

        created.Should().ContainSingle();
        created[0].Columns.Select(c => c.Name).Should().BeEquivalentTo("Id", "FriendlyName", "Xml");
        created[0].Columns.Single(c => c.Name == "Xml").IsNullable.Should().BeFalse();
        created[0].PrimaryKey!.Columns.Should().Equal("Id");
    }

    [Fact] // invariant (NFR-8): the key ring is not account data, so the account-filtered model never maps its table
    public void Model_ShouldNotMapTheKeyRingTable_WhenTheModelIsBuilt()
    {
        using var db = CreateContext();

        db.Model.GetEntityTypes().Select(t => t.GetTableName())
            .Should().NotContain(PostgresDataProtectionKeyStore.TableName);
    }

    private static ApiaryCopilotDbContext CreateContext()
    {
        var provider = new ServiceCollection()
            .AddInfrastructure(SyntheticConnectionString)
            .BuildServiceProvider();
        return provider.CreateScope().ServiceProvider.GetRequiredService<ApiaryCopilotDbContext>();
    }
}
